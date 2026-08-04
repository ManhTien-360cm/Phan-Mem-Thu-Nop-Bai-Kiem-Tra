using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamTransfer.Infrastructure.Services;

public sealed class QuizService(
    AppDbContext db,
    QuizProjectionOutbox projectionOutbox,
    IStoragePaths? paths = null,
    ILogger<QuizService>? logger = null) : IQuizService
{
    public async Task<IReadOnlyList<QuizAttemptDto>> ListAttemptsForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var sessionExists = await db.ExamSessionsSet.AsNoTracking()
            .AnyAsync(x => x.Id == sessionId, cancellationToken);
        if (!sessionExists) throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phiên thi.", 404);

        var attempts = await db.QuizAttemptsSet.AsNoTracking()
            .Include(x => x.Answers)
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(cancellationToken);
        return attempts
            .OrderByDescending(x => x.StartedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(ToTeacherDto)
            .ToArray();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan OfflineSyncGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(20);
    private const int MaxImportBytes = 10 * 1024 * 1024;

    public async Task<QuizImportPreviewDto> PreviewImportAsync(
        Guid examId,
        Guid teacherId,
        QuizImportPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var exam = await db.ExamsSet.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == examId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy đề thi.", 404);
        if (exam.Status != ExamStatus.Draft)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ xem trước nguồn khi đề đang ở trạng thái nháp.", 409);
        if (exam.DeliveryType != ExamDeliveryType.MultipleChoice)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Hãy chọn loại bài Trắc nghiệm và lưu trước khi nhập nguồn.", 409);
        var bytes = DecodeImportBytes(request.Base64Content);
        var parsed = QuizDocumentParser.Parse(request.FileName, bytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var mimeType = MimeTypeFor(request.FileName);
        var existing = await db.QuizQuestionsSet.AsNoTracking()
            .AnyAsync(x => x.ExamId == examId && x.Version == exam.Version, cancellationToken);
        if (parsed.Errors.Count > 0)
            return new(string.Empty, request.FileName, mimeType, sha256, parsed.Document.Questions.Count,
                parsed.Document.Questions.Sum(x => x.Points), PreviewQuestions(parsed.Document),
                parsed.Warnings, parsed.Errors, DateTimeOffset.UtcNow, existing);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var temporaryRoot = paths?.TemporaryRoot
            ?? Path.Combine(Path.GetTempPath(), "ExamTransfer", "quiz-import");
        Directory.CreateDirectory(temporaryRoot);
        var temporaryPath = Path.Combine(temporaryRoot,
            $"quiz-preview-{Guid.NewGuid():N}{Path.GetExtension(request.FileName).ToLowerInvariant()}");
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.Add(PreviewLifetime);
        db.QuizImportPreviewsSet.Add(new QuizImportPreview
        {
            TokenHash = HashToken(token),
            ExamId = exam.Id,
            ExamVersion = exam.Version,
            TeacherId = teacherId,
            ExamRowVersion = exam.RowVersion,
            OriginalName = Path.GetFileName(request.FileName),
            MimeType = mimeType,
            SizeBytes = bytes.LongLength,
            Sha256 = sha256,
            TemporaryPath = temporaryPath,
            DocumentJson = JsonSerializer.Serialize(parsed.Document, Json),
            WarningsJson = JsonSerializer.Serialize(parsed.Warnings, Json),
            ExpiresAtUtc = expiresAt
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(token, Path.GetFileName(request.FileName), mimeType, sha256,
            parsed.Document.Questions.Count, parsed.Document.Questions.Sum(x => x.Points),
            PreviewQuestions(parsed.Document), parsed.Warnings, [], expiresAt, existing);
    }

    public async Task<QuizImportResultDto> CommitImportAsync(
        Guid examId,
        Guid teacherId,
        QuizImportCommitRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewToken))
            throw new ApiException(ErrorCodes.ValidationFailed, "Preview token là bắt buộc.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        string? committedPath = null;
        try
        {
            var preview = await db.QuizImportPreviewsSet
                .FirstOrDefaultAsync(x => x.TokenHash == HashToken(request.PreviewToken), cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Preview token không tồn tại.", 404);
            if (preview.ExamId != examId || preview.TeacherId != teacherId)
                throw new ApiException(ErrorCodes.Forbidden, "Preview token không thuộc giáo viên hoặc bài kiểm tra này.", 403);
            if (preview.CommittedAtUtc.HasValue)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Preview token đã được commit.", 409);
            if (preview.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Preview token đã hết hạn; hãy xem trước lại.", 409);
            var exam = await db.ExamsSet
                .Include(x => x.QuizQuestions).ThenInclude(x => x.Choices)
                .Include(x => x.QuizImportSources)
                .FirstOrDefaultAsync(x => x.Id == examId, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy đề thi.", 404);
            if (exam.Status != ExamStatus.Draft || exam.DeliveryType != ExamDeliveryType.MultipleChoice)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài kiểm tra không còn là đề trắc nghiệm nháp.", 409);
            if (exam.Version != preview.ExamVersion
                || exam.RowVersion != preview.ExamRowVersion
                || exam.RowVersion != request.ExamRowVersion)
                throw new ApiException(ErrorCodes.ConcurrencyConflict, "Bài kiểm tra đã thay đổi sau khi xem trước.", 409);
            if (!File.Exists(preview.TemporaryPath))
                throw new ApiException(ErrorCodes.InvalidStateTransition, "File preview tạm không còn tồn tại; hãy xem trước lại.", 409);
            await using (var source = File.OpenRead(preview.TemporaryPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).ToLowerInvariant();
                if (!actualHash.Equals(preview.Sha256, StringComparison.Ordinal))
                    throw new ApiException(ErrorCodes.ValidationFailed, "Hash file preview không còn khớp.");
            }
            var document = JsonSerializer.Deserialize<QuizImportDocument>(preview.DocumentJson, Json)
                ?? throw new ApiException(ErrorCodes.ValidationFailed, "Dữ liệu preview không hợp lệ.");
            Validate(document);
            var replacedQuestions = exam.QuizQuestions.Where(x => x.Version == exam.Version).ToList();
            if (replacedQuestions.Count > 0 && !request.ConfirmReplace)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Đề đã có câu hỏi; cần xác nhận thay thế.", 409);
            var replacedChoices = replacedQuestions.SelectMany(x => x.Choices).ToList();
            db.QuizQuestionsSet.RemoveRange(replacedQuestions);
            AddQuestions(exam, document);

            var oldSources = exam.QuizImportSources
                .Where(x => x.ExamVersion == exam.Version)
                .ToList();
            var sourceCandidates = oldSources
                .Select(x => new
                {
                    Entity = x,
                    FullPath = TryResolveSourcePath(x.RelativePath)
                })
                .OrderByDescending(x =>
                    x.Entity.Status == "Committed"
                    && x.FullPath is not null
                    && File.Exists(x.FullPath))
                .ThenByDescending(x => x.Entity.ImportedAtUtc)
                .ThenByDescending(x => x.Entity.UpdatedAtUtc)
                .ThenByDescending(x => x.Entity.Id)
                .ToList();
            var sourceEntity = sourceCandidates.FirstOrDefault()?.Entity;
            var oldLocalPaths = sourceCandidates
                .Select(x => x.FullPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sourceEntity is not null)
                db.QuizImportSourcesSet.RemoveRange(
                    oldSources.Where(x => x.Id != sourceEntity.Id));
            var destinationRoot = paths?.ExamVersionRoot(exam.Id, exam.Version)
                ?? Path.Combine(Path.GetTempPath(), "ExamTransfer", "exams", exam.Id.ToString("N"), $"v{exam.Version}");
            destinationRoot = Path.Combine(destinationRoot, "quiz-source");
            Directory.CreateDirectory(destinationRoot);
            committedPath = Path.Combine(destinationRoot,
                $"{Guid.NewGuid():N}{Path.GetExtension(preview.OriginalName).ToLowerInvariant()}");
            File.Copy(preview.TemporaryPath, committedPath, overwrite: false);
            var importedAtUtc = DateTimeOffset.UtcNow;
            if (sourceEntity is null)
            {
                sourceEntity = new QuizImportSource
                {
                    ExamId = exam.Id,
                    ExamVersion = exam.Version
                };
                db.QuizImportSourcesSet.Add(sourceEntity);
            }
            sourceEntity.OriginalName = preview.OriginalName;
            sourceEntity.MimeType = preview.MimeType;
            sourceEntity.SizeBytes = preview.SizeBytes;
            sourceEntity.Sha256 = preview.Sha256;
            sourceEntity.RelativePath = paths is null
                ? committedPath
                : Path.GetRelativePath(paths.RootPath, committedPath);
            sourceEntity.Status = "Committed";
            sourceEntity.CreatedBy = teacherId;
            sourceEntity.ImportedAtUtc = importedAtUtc;
            sourceEntity.UpdatedAtUtc = importedAtUtc;
            preview.CommittedAtUtc = importedAtUtc;
            await db.SaveChangesAsync(cancellationToken);

            foreach (var choice in replacedChoices)
                await projectionOutbox.EnqueueChoiceDeleteAsync(choice.Id, cancellationToken);
            foreach (var question in replacedQuestions)
                await projectionOutbox.EnqueueQuestionDeleteAsync(question.Id, cancellationToken);
            foreach (var question in await db.QuizQuestionsSet.AsNoTracking().Include(x => x.Choices)
                         .Where(x => x.ExamId == exam.Id && x.Version == exam.Version).ToListAsync(cancellationToken))
            {
                await projectionOutbox.EnqueueQuestionUpsertAsync(question, cancellationToken);
                foreach (var choice in question.Choices)
                    await projectionOutbox.EnqueueChoiceUpsertAsync(choice, cancellationToken);
            }
            await projectionOutbox.EnqueueSourceUpsertAsync(
                sourceEntity,
                committedPath,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            TryDelete(preview.TemporaryPath, "file preview tạm");
            foreach (var oldLocalPath in oldLocalPaths.Where(x =>
                         !string.Equals(
                             Path.GetFullPath(x),
                             Path.GetFullPath(committedPath),
                             StringComparison.OrdinalIgnoreCase)))
                TryDelete(oldLocalPath, "file nguồn trắc nghiệm cũ");
            return new(exam.Id, exam.Version, document.Questions.Count, document.Questions.Sum(x => x.Points));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (committedPath is not null)
                TryDelete(committedPath, "file nguồn mới sau rollback");
            throw;
        }
    }

    public Task<QuizImportResultDto> ImportAsync(
        Guid examId,
        QuizImportFileRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<QuizImportResultDto>(new ApiException(
            ErrorCodes.QuizImportLegacyDisabled,
            "Endpoint nhập JSON/CSV/XLSX đã ngừng hoạt động; hãy dùng preview và commit nguồn DOCX/PDF.",
            410));

    public async Task<QuizAttemptDto> StartOrGetAttemptAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken)
    {
        var existing = await db.QuizAttemptsSet.Include(x => x.Answers)
            .FirstOrDefaultAsync(x => x.SessionId == sessionId && x.ParticipantId == participantId, cancellationToken);
        if (existing is not null) return ToStudentDto(existing);

        var participant = await db.SessionParticipantsSet.AsNoTracking()
            .Include(x => x.Session).ThenInclude(x => x.Exam).ThenInclude(x => x.QuizQuestions).ThenInclude(x => x.Choices)
            .FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lượt dự thi.", 404);
        var session = participant.Session;
        if (participant.Status != ParticipantStatus.Approved)
            throw new ApiException(ErrorCodes.Forbidden, "Lượt dự thi chưa được duyệt.", 403);
        if (session.Status is not (SessionStatus.InProgress or SessionStatus.Paused))
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài trắc nghiệm chưa bắt đầu.", 409);
        if (session.DeliveryTypeSnapshot != ExamDeliveryType.MultipleChoice)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Đề này không phải đề trắc nghiệm.", 409);
        if (session.SupervisionModeSnapshot != SupervisionMode.Standard)
            throw new ApiException(ErrorCodes.Forbidden, "Phiên trắc nghiệm không có giám sát chuẩn hợp lệ.", 403);
        var latestPolicyVersion = await db.ControlPoliciesSet.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .MaxAsync(x => (int?)x.Version, cancellationToken);
        var supervisionReady = latestPolicyVersion.HasValue
            && await db.DevicePolicyStatusesSet.AsNoTracking().AnyAsync(
                x => x.SessionId == session.Id
                    && x.ParticipantId == participant.Id
                    && x.PolicyVersion == latestPolicyVersion.Value
                    && x.Status == PolicyApplyStatus.Applied,
                cancellationToken);
        if (!supervisionReady)
            throw new ApiException(ErrorCodes.Forbidden, "Thiết bị chưa áp dụng xong chính sách giám sát chuẩn.", 403);
        var questions = session.Exam.QuizQuestions.Where(x => x.Version == session.ExamVersionSnapshot).OrderBy(x => x.Order).Select(ToQuestionDto).ToList();
        if (questions.Count == 0) throw new ApiException(ErrorCodes.InvalidStateTransition, "Đề chưa có câu hỏi trắc nghiệm.", 409);
        if (questions.Sum(x => x.Points) != 10.00m)
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Đề trắc nghiệm chưa được chuẩn hóa về thang điểm 10.00; hãy nhập lại hoặc nhân bản đề trước khi mở lượt làm bài.",
                409);
        var deadline = session.StartedAtUtc!.Value.AddMinutes(session.Exam.DurationMinutes + participant.ExtraTimeMinutes);
        if (DateTimeOffset.UtcNow > deadline) throw new ApiException(ErrorCodes.DeadlinePassed, "Đã hết thời gian làm bài.", 409);
        var attempt = new QuizAttempt
        {
            SessionId = sessionId, ParticipantId = participantId, ExamVersion = session.ExamVersionSnapshot,
            StartedAtUtc = DateTimeOffset.UtcNow, DeadlineUtc = deadline,
            MaxScore = 10.00m, SnapshotJson = JsonSerializer.Serialize(questions, Json),
            ResultPolicySnapshot = session.QuizResultPolicySnapshot
        };
        db.QuizAttemptsSet.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        await projectionOutbox.EnqueueAttemptUpsertAsync(attempt, cancellationToken);
        return ToStudentDto(attempt);
    }

    public async Task<QuizAttemptDto?> GetAttemptAsync(
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        var attempt = await db.QuizAttemptsSet.AsNoTracking()
            .Include(x => x.Answers)
            .FirstOrDefaultAsync(
                x => x.SessionId == sessionId && x.ParticipantId == participantId,
                cancellationToken);
        return attempt is null ? null : ToStudentDto(attempt);
    }

    public async Task<SyncQuizAnswersResultDto> SyncAnswersAsync(Guid attemptId, Guid participantId, SyncQuizAnswersRequest request, CancellationToken cancellationToken)
    {
        var attempt = await OwnedAttempt(attemptId, participantId, cancellationToken);
        if (attempt.Status == QuizAttemptStatus.Finalized)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài đã chốt nên không thể sửa đáp án.", 409);
        if (DateTimeOffset.UtcNow > attempt.DeadlineUtc + OfflineSyncGrace
            || request.Answers.Any(x => x.ClientUpdatedAtUtc > attempt.DeadlineUtc))
            throw new ApiException(ErrorCodes.DeadlinePassed, "Đáp án được tạo sau deadline hoặc đã quá thời gian đồng bộ ngoại tuyến.", 409);
        var questions = Snapshot(attempt);
        var byId = questions.ToDictionary(x => x.Id);
        foreach (var incoming in request.Answers)
        {
            if (!byId.TryGetValue(incoming.QuestionId, out var question))
                throw new ApiException(ErrorCodes.ValidationFailed, "Đáp án chứa câu hỏi không thuộc đề đã chụp.");
            var selected = incoming.ChoiceIds.Distinct().ToList();
            if (selected.Any(id => question.Choices.All(x => x.Id != id)) || (!question.Multiple && selected.Count > 1))
                throw new ApiException(ErrorCodes.ValidationFailed, "Lựa chọn không hợp lệ cho câu hỏi.");
            var answer = attempt.Answers.FirstOrDefault(x => x.QuestionId == incoming.QuestionId);
            if (answer is not null && incoming.Revision <= answer.Revision) continue;
            if (answer is null)
            {
                answer = new QuizAnswer { AttemptId = attempt.Id, QuestionId = incoming.QuestionId };
                db.QuizAnswersSet.Add(answer);
            }
            answer.ChoiceIdsJson = JsonSerializer.Serialize(selected, Json);
            answer.Revision = incoming.Revision;
            answer.ClientUpdatedAtUtc = incoming.ClientUpdatedAtUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var answer in attempt.Answers)
            await projectionOutbox.EnqueueAnswerUpsertAsync(answer, cancellationToken);
        return new(attempt.Id, attempt.Answers.Select(ToAnswerDto).ToList(), DateTimeOffset.UtcNow);
    }

    public async Task<QuizAttemptDto> FinalizeAsync(Guid attemptId, Guid participantId, FinalizeQuizAttemptRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new ApiException(ErrorCodes.ValidationFailed, "Idempotency key không hợp lệ.");
        var attempt = await OwnedAttempt(attemptId, participantId, cancellationToken);
        if (attempt.Status == QuizAttemptStatus.Finalized) return ToStudentDto(attempt);
        var questionIds = Snapshot(attempt).Select(x => x.Id).ToList();
        var questions = await db.QuizQuestionsSet.AsNoTracking().Include(x => x.Choices)
            .Where(x => questionIds.Contains(x.Id)).ToListAsync(cancellationToken);
        decimal score = 0;
        foreach (var question in questions)
        {
            var expected = question.Choices.Where(x => x.IsCorrect).Select(x => x.Id).Order().ToArray();
            var answer = attempt.Answers.FirstOrDefault(x => x.QuestionId == question.Id);
            var actual = answer is null ? [] : JsonSerializer.Deserialize<List<Guid>>(answer.ChoiceIdsJson, Json)!.Distinct().Order().ToArray();
            if (expected.SequenceEqual(actual)) score += question.Points;
        }
        attempt.Score = score;
        attempt.AutoScore = score;
        attempt.MaxScore = 10.00m;
        attempt.Status = QuizAttemptStatus.Finalized;
        attempt.FinalizedAtUtc = DateTimeOffset.UtcNow;
        attempt.GradingStatus = GradingStatus.Graded;
        attempt.GradedAtUtc = attempt.FinalizedAtUtc;
        attempt.FinalizeIdempotencyKey = request.IdempotencyKey.Trim();
        await db.SaveChangesAsync(cancellationToken);
        await projectionOutbox.EnqueueAttemptUpsertAsync(attempt, cancellationToken);
        return ToStudentDto(attempt);
    }

    private async Task<QuizAttempt> OwnedAttempt(Guid attemptId, Guid participantId, CancellationToken ct) =>
        await db.QuizAttemptsSet.Include(x => x.Answers).FirstOrDefaultAsync(x => x.Id == attemptId && x.ParticipantId == participantId, ct)
        ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài làm trắc nghiệm.", 404);

    private static QuizAttemptDto ToStudentDto(QuizAttempt attempt)
    {
        var scoreVisible = attempt.Status == QuizAttemptStatus.Finalized
            && attempt.GradingStatus == GradingStatus.Returned
            && attempt.ReturnedAtUtc.HasValue;
        return new(
            attempt.Id, attempt.SessionId, attempt.ParticipantId, attempt.Status, attempt.ExamVersion,
            attempt.StartedAtUtc, attempt.DeadlineUtc, attempt.FinalizedAtUtc,
            scoreVisible ? attempt.Score : null, attempt.MaxScore,
            Snapshot(attempt), attempt.Answers.Select(ToAnswerDto).ToList(),
            scoreVisible, attempt.ResultPolicySnapshot);
    }

    private static QuizAttemptDto ToTeacherDto(QuizAttempt attempt) => new(
        attempt.Id, attempt.SessionId, attempt.ParticipantId, attempt.Status, attempt.ExamVersion,
        attempt.StartedAtUtc, attempt.DeadlineUtc, attempt.FinalizedAtUtc, attempt.Score, attempt.MaxScore,
        Snapshot(attempt), attempt.Answers.Select(ToAnswerDto).ToList(),
        attempt.Status == QuizAttemptStatus.Finalized, attempt.ResultPolicySnapshot);
    private static QuizAnswerDto ToAnswerDto(QuizAnswer x) => new(x.QuestionId, JsonSerializer.Deserialize<List<Guid>>(x.ChoiceIdsJson, Json) ?? [], x.Revision, x.ClientUpdatedAtUtc);
    private static IReadOnlyList<QuizQuestionDto> Snapshot(QuizAttempt x)
    {
        using var document = JsonDocument.Parse(x.SnapshotJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        return document.RootElement.EnumerateArray().Select(question =>
        {
            var choices = question.TryGetProperty("choices", out var choiceRows)
                && choiceRows.ValueKind == JsonValueKind.Array
                ? choiceRows.EnumerateArray().Select(choice => new QuizChoiceDto(
                    choice.GetProperty("id").GetGuid(),
                    StringProperty(choice, "text", "choiceText"),
                    IntProperty(choice, "order", "sortOrder"))).ToList()
                : [];
            return new QuizQuestionDto(
                question.GetProperty("id").GetGuid(),
                StringProperty(question, "text", "questionText"),
                IntProperty(question, "order", "sortOrder"),
                question.TryGetProperty("points", out var points) ? points.GetDecimal() : 0,
                question.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean(),
                choices);
        }).ToList();
    }

    private static string StringProperty(JsonElement element, string primary, string fallback) =>
        element.TryGetProperty(primary, out var value)
            ? value.GetString() ?? string.Empty
            : element.TryGetProperty(fallback, out value)
                ? value.GetString() ?? string.Empty
                : string.Empty;

    private static int IntProperty(JsonElement element, string primary, string fallback) =>
        element.TryGetProperty(primary, out var value)
            ? value.GetInt32()
            : element.TryGetProperty(fallback, out value)
                ? value.GetInt32()
                : 0;
    private static QuizQuestionDto ToQuestionDto(QuizQuestion x) => new(x.Id, x.Text, x.Order, x.Points, x.Multiple, x.Choices.OrderBy(c => c.Order).Select(c => new QuizChoiceDto(c.Id, c.Text, c.Order)).ToList());

    private void AddQuestions(Exam exam, QuizImportDocument document)
    {
        var order = 0;
        foreach (var input in document.Questions)
        {
            var question = new QuizQuestion
            {
                ExamId = exam.Id,
                Version = exam.Version,
                Order = ++order,
                Text = input.Text.Trim(),
                Points = input.Points,
                Multiple = input.Multiple
            };
            for (var index = 0; index < input.Choices.Count; index++)
                question.Choices.Add(new QuizChoice
                {
                    Order = index + 1,
                    Text = input.Choices[index].Trim(),
                    IsCorrect = input.CorrectChoiceIndexes.Contains(index)
                });
            db.QuizQuestionsSet.Add(question);
        }
    }

    private static IReadOnlyList<QuizQuestionDto> PreviewQuestions(QuizImportDocument document) =>
        document.Questions.Select((question, questionIndex) =>
        {
            var choices = question.Choices.Select((text, choiceIndex) =>
                new QuizChoiceDto(Guid.NewGuid(), text, choiceIndex + 1)).ToList();
            return new QuizQuestionDto(Guid.NewGuid(), question.Text, questionIndex + 1,
                question.Points, question.Multiple, choices);
        }).ToList();

    private static byte[] DecodeImportBytes(string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Nội dung tệp không phải Base64 hợp lệ.");
        }
        if (bytes.Length is 0 or > MaxImportBytes)
            throw new ApiException(ErrorCodes.ValidationFailed, "Tệp câu hỏi phải có dung lượng từ 1 byte đến 10 MB.");
        return bytes;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string MimeTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private string? TryResolveSourcePath(string relativePath)
    {
        try
        {
            var fullPath = paths is null || Path.IsPathRooted(relativePath)
                ? Path.GetFullPath(relativePath)
                : Path.GetFullPath(Path.Combine(paths.RootPath, relativePath));
            if (paths is null)
                return fullPath;
            var root = Path.GetFullPath(paths.RootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            logger?.LogWarning(
                ex,
                "Không thể chuẩn hóa đường dẫn nguồn quiz cũ {RelativePath}.",
                relativePath);
            return null;
        }
    }

    private void TryDelete(string path, string description)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Không thể xóa {Description} tại {Path}; metadata đã đạt trạng thái cuối.",
                description,
                path);
        }
    }

    private static QuizImportDocument ParseDocument(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".json" => JsonSerializer.Deserialize<QuizImportDocument>(bytes, Json) ?? throw new InvalidDataException(),
                ".csv" => FromRows(ParseCsv(Encoding.UTF8.GetString(bytes))),
                ".xlsx" => FromRows(ParseXlsx(bytes)),
                _ => throw new ApiException(ErrorCodes.ValidationFailed, "Chỉ hỗ trợ tệp JSON, CSV hoặc XLSX có cấu trúc chính thức.")
            };
        }
        catch (ApiException) { throw; }
        catch (Exception ex) { throw new ApiException(ErrorCodes.ValidationFailed, "Không đọc được cấu trúc tệp câu hỏi.", details: ex.Message); }
    }

    private static void Validate(QuizImportDocument document)
    {
        if (document.Questions.Count is < 1 or > 500) throw new ApiException(ErrorCodes.ValidationFailed, "Đề phải có từ 1 đến 500 câu hỏi.");
        if (document.Questions.Sum(x => x.Points) != 10.00m)
            throw new ApiException(ErrorCodes.ValidationFailed, "Tổng điểm trắc nghiệm phải chính xác bằng 10.00.");
        foreach (var q in document.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.Text)
                || q.Text.Length > 5000
                || q.Points <= 0
                || decimal.Round(q.Points, 2, MidpointRounding.ToEven) != q.Points)
                throw new ApiException(ErrorCodes.ValidationFailed, "Nội dung hoặc điểm câu hỏi không hợp lệ.");
            if (q.Choices.Count is < 2 or > 10 || q.Choices.Any(string.IsNullOrWhiteSpace))
                throw new ApiException(ErrorCodes.ValidationFailed, "Mỗi câu phải có từ 2 đến 10 lựa chọn.");
            var correct = q.CorrectChoiceIndexes.Distinct().ToList();
            if (correct.Count == 0 || correct.Any(x => x < 0 || x >= q.Choices.Count) || (!q.Multiple && correct.Count != 1))
                throw new ApiException(ErrorCodes.ValidationFailed, "Đáp án đúng không hợp lệ.");
        }
    }

    private static QuizImportDocument FromRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2) throw new InvalidDataException();
        var headers = rows[0].Select((x, i) => (x.Trim().ToLowerInvariant(), i)).ToDictionary(x => x.Item1, x => x.i);
        string Cell(IReadOnlyList<string> row, string name) => headers.TryGetValue(name, out var i) && i < row.Count ? row[i].Trim() : string.Empty;
        var result = new List<QuizImportQuestion>();
        foreach (var row in rows.Skip(1).Where(x => x.Any(v => !string.IsNullOrWhiteSpace(v))))
        {
            var choices = new[] { "choice_a", "choice_b", "choice_c", "choice_d", "choice_e", "choice_f", "choice_g", "choice_h", "choice_i", "choice_j" }.Select(x => Cell(row, x)).TakeWhile(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var correct = Cell(row, "correct").Split(['|', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => char.IsLetter(x[0]) ? char.ToUpperInvariant(x[0]) - 'A' : int.Parse(x, CultureInfo.InvariantCulture) - 1).ToList();
            result.Add(new(Cell(row, "question"), decimal.Parse(Cell(row, "points"), CultureInfo.InvariantCulture), bool.TryParse(Cell(row, "multiple"), out var multiple) ? multiple : correct.Count > 1, choices, correct));
        }
        return new(result);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if ((c == '\n' || c == '\r') && !quoted) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Sum(x => x.Length) > 30L * 1024 * 1024 || zip.Entries.Any(x => x.Length > 20L * 1024 * 1024))
            throw new InvalidDataException("XLSX giải nén vượt giới hạn an toàn.");
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } stringsEntry
            ? XDocument.Load(stringsEntry.Open()).Descendants().Where(x => x.Name.LocalName == "si").Select(x => string.Concat(x.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToList()
            : [];
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException();
        var document = XDocument.Load(sheet.Open()); var rows = new List<IReadOnlyList<string>>();
        foreach (var row in document.Descendants().Where(x => x.Name.LocalName == "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? "A1"; var column = reference.TakeWhile(char.IsLetter).Aggregate(0, (n, c) => n * 26 + char.ToUpperInvariant(c) - 'A' + 1) - 1;
                while (cells.Count <= column) cells.Add(string.Empty);
                var raw = cell.Descendants().FirstOrDefault(x => x.Name.LocalName is "v" or "t")?.Value ?? string.Empty;
                cells[column] = cell.Attribute("t")?.Value == "s" && int.TryParse(raw, out var index) ? shared[index] : raw;
            }
            rows.Add(cells);
        }
        return rows;
    }
}
