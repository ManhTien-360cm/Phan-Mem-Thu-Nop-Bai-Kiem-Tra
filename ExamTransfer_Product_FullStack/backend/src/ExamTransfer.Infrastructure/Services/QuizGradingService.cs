using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class QuizGradingService(
    AppDbContext db,
    IAuditService audit,
    IOutboxService outbox,
    ICloudAdapter? cloud = null) : IQuizGradingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<GradingWorkItemDto>> GetWorkItemsAsync(
        GradingStatus? status,
        int page,
        int pageSize,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var actor = await RequireActorAsync(actorId, organizationId, cancellationToken);
        var files = await db.SubmissionsSet.AsNoTracking()
            .Include(x => x.Participant)
            .Include(x => x.Files)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .Where(x => x.IsOfficial
                && (x.Status == SubmissionStatus.Submitted || x.Status == SubmissionStatus.LateSubmitted))
            .ToListAsync(cancellationToken);
        var fileGradeBySubmission = await db.GradesSet.AsNoTracking()
            .Where(x => files.Select(s => s.Id).Contains(x.SubmissionId))
            .ToDictionaryAsync(x => x.SubmissionId, cancellationToken);
        var quizzes = await db.QuizAttemptsSet.AsNoTracking()
            .Include(x => x.Participant)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .Where(x => x.Status == QuizAttemptStatus.Finalized)
            .ToListAsync(cancellationToken);

        var items = new List<GradingWorkItemDto>();
        foreach (var submission in files)
        {
            if (!await CanAccessExamAsync(submission.Session.Exam, actor, cancellationToken))
                continue;
            fileGradeBySubmission.TryGetValue(submission.Id, out var grade);
            items.Add(new(
                submission.Id,
                GradingWorkItemType.FileSubmission,
                submission.SessionId,
                submission.ParticipantId,
                submission.Participant.StudentCode,
                submission.Participant.DisplayName,
                submission.Session.Exam.Title,
                submission.ServerReceivedAtUtc ?? submission.ClientSubmittedAtUtc,
                grade?.Status ?? GradingStatus.NotGraded,
                null,
                grade?.Score,
                10.00m,
                submission.Files.OrderBy(x => x.OriginalName).Select(x => (Guid?)x.Id).FirstOrDefault()));
        }
        foreach (var attempt in quizzes)
        {
            if (!await CanAccessExamAsync(attempt.Session.Exam, actor, cancellationToken))
                continue;
            items.Add(new(
                attempt.Id,
                GradingWorkItemType.QuizAttempt,
                attempt.SessionId,
                attempt.ParticipantId,
                attempt.Participant.StudentCode,
                attempt.Participant.DisplayName,
                attempt.Session.Exam.Title,
                attempt.FinalizedAtUtc ?? attempt.UpdatedAtUtc,
                attempt.GradingStatus,
                attempt.AutoScore,
                attempt.Score,
                attempt.MaxScore));
        }
        if (status.HasValue)
            items = items.Where(x => x.Status == status.Value).ToList();
        var ordered = items.OrderByDescending(x => x.SubmittedAtUtc).ThenBy(x => x.StudentCode).ToList();
        return new(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            page,
            pageSize,
            ordered.Count);
    }

    public async Task<QuizGradeDetailDto> GetAsync(
        Guid attemptId,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await RequireGradeableAttemptAsync(
            attemptId,
            actorId,
            organizationId,
            cancellationToken);
        return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
    }

    public async Task<QuizGradeDetailDto> SaveAsync(
        Guid attemptId,
        SaveQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await RequireGradeableAttemptAsync(
            attemptId,
            actorId,
            organizationId,
            cancellationToken);
        if (attempt.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloudResult = await RequireCloud().SavePublicQuizGradeAsync(
                attempt.Id,
                request.Score,
                request.GeneralComment,
                RequireCloudVersion(request.RowVersion),
                RequireMutationRequestId(request.MutationRequestId),
                cancellationToken);
            ApplyCloudMutation(attempt, cloudResult);
            await db.SaveChangesAsync(cancellationToken);
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        }

        var requestHash = HashRequest(new
        {
            attemptId,
            request.Score,
            generalComment = Normalize(request.GeneralComment),
            request.RowVersion
        });
        var cached = await FindReceiptAsync(
            request.MutationRequestId,
            attemptId,
            actorId,
            "SaveQuizGrade",
            requestHash,
            cancellationToken);
        if (cached is not null)
            return cached;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        EnsureConcurrency(attempt, request.RowVersion);
        if (attempt.GradingStatus == GradingStatus.Returned)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Kết quả đã công bố; cần mở lại trước khi sửa.", 409);
        var authoritative = await QuizGradeAuthoritativeScoring.CalculateAsync(db, attempt, cancellationToken);
        if (request.Score.HasValue && request.Score.Value != authoritative.Score)
            throw new ApiException(ErrorCodes.ValidationFailed, "Điểm gửi từ client không khớp kết quả authoritative.");

        var before = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        attempt.AutoScore = authoritative.Score;
        attempt.Score = authoritative.Score;
        attempt.MaxScore = authoritative.MaxScore;
        attempt.GeneralComment = Normalize(request.GeneralComment);
        attempt.GradingStatus = GradingStatus.Graded;
        attempt.ReturnedAtUtc = null;
        attempt.GraderId = actorId;
        attempt.GradedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var result = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        await audit.WriteAsync(
            "QuizGradeSaved",
            nameof(QuizAttempt),
            attempt.Id.ToString(),
            attempt.SessionId,
            before,
            new { ActorId = actorId, Grade = result, Summary = authoritative },
            cancellationToken);
        await EnqueueAsync(attempt, cancellationToken);
        await StoreReceiptAsync(
            request.MutationRequestId,
            attemptId,
            actorId,
            "SaveQuizGrade",
            requestHash,
            result,
            null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<QuizGradeDetailDto> ReturnAsync(
        Guid attemptId,
        ReturnQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await RequireGradeableAttemptAsync(
            attemptId,
            actorId,
            organizationId,
            cancellationToken);
        if (attempt.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloudResult = await RequireCloud().ReturnPublicQuizGradeAsync(
                attempt.Id,
                request.Message,
                RequireCloudVersion(request.RowVersion),
                RequireMutationRequestId(request.MutationRequestId),
                cancellationToken);
            ApplyCloudMutation(attempt, cloudResult);
            await db.SaveChangesAsync(cancellationToken);
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        }

        var requestHash = HashRequest(new
        {
            attemptId,
            message = Normalize(request.Message),
            request.RowVersion
        });
        var cached = await FindReceiptAsync(
            request.MutationRequestId,
            attemptId,
            actorId,
            "ReturnQuizGrade",
            requestHash,
            cancellationToken);
        if (cached is not null)
            return cached;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (attempt.GradingStatus == GradingStatus.Returned && request.MutationRequestId == Guid.Empty)
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        EnsureConcurrency(attempt, request.RowVersion);
        if (attempt.GradingStatus != GradingStatus.Graded)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ có thể trả kết quả Quiz đã chấm.", 409);
        var authoritative = await QuizGradeAuthoritativeScoring.CalculateAsync(db, attempt, cancellationToken);
        if (attempt.AutoScore != authoritative.Score
            || attempt.Score != authoritative.Score
            || attempt.MaxScore != authoritative.MaxScore)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Kết quả Quiz chưa khớp dữ liệu answer authoritative.");
        }
        var returnedAt = DateTimeOffset.UtcNow;
        attempt.GradingStatus = GradingStatus.Returned;
        attempt.ReturnedAtUtc = returnedAt;
        attempt.GraderId = actorId;
        attempt.GradedAtUtc ??= returnedAt;
        var session = attempt.Session;
        session.Sequence++;
        var eventId = Guid.NewGuid();
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.QuizGradeReturned,
            attempt.SessionId,
            session.Sequence,
            participantId: attempt.ParticipantId,
            attemptId: attempt.Id,
            message: request.Message,
            score: attempt.Score,
            maxScore: attempt.MaxScore,
            occurredAtUtc: returnedAt,
            eventId: eventId);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "QuizGradeReturned",
            nameof(QuizAttempt),
            attempt.Id.ToString(),
            attempt.SessionId,
            null,
            new { ActorId = actorId, attempt.Score, attempt.MaxScore, Message = Normalize(request.Message), returnedAt },
            cancellationToken);
        await EnqueueAsync(attempt, cancellationToken);
        var result = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        await StoreReceiptAsync(
            request.MutationRequestId,
            attemptId,
            actorId,
            "ReturnQuizGrade",
            requestHash,
            result,
            eventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<QuizGradeDetailDto> ReopenAsync(
        Guid attemptId,
        ReopenQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(ErrorCodes.ValidationFailed, "Phải có lý do mở lại kết quả.");
        var attempt = await RequireGradeableAttemptAsync(
            attemptId,
            actorId,
            organizationId,
            cancellationToken);
        if (attempt.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloudResult = await RequireCloud().ReopenPublicQuizGradeAsync(
                attempt.Id,
                request.Reason,
                RequireCloudVersion(request.RowVersion),
                RequireMutationRequestId(request.MutationRequestId),
                cancellationToken);
            ApplyCloudMutation(attempt, cloudResult);
            await db.SaveChangesAsync(cancellationToken);
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        }

        var requestHash = HashRequest(new
        {
            attemptId,
            reason = request.Reason.Trim(),
            request.RowVersion
        });
        var cached = await FindReceiptAsync(
            request.MutationRequestId,
            attemptId,
            actorId,
            "ReopenQuizGrade",
            requestHash,
            cancellationToken);
        if (cached is not null)
            return cached;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        EnsureConcurrency(attempt, request.RowVersion);
        if (attempt.GradingStatus != GradingStatus.Returned)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ mở lại kết quả đã công bố.", 409);
        var authoritative = await QuizGradeAuthoritativeScoring.CalculateAsync(db, attempt, cancellationToken);
        if (attempt.AutoScore != authoritative.Score
            || attempt.Score != authoritative.Score
            || attempt.MaxScore != authoritative.MaxScore)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Kết quả Quiz đã trả không khớp dữ liệu answer authoritative.");
        }
        var before = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        attempt.GradingStatus = GradingStatus.Graded;
        attempt.ReturnedAtUtc = null;
        attempt.GraderId = actorId;
        attempt.Session.Sequence++;
        var eventId = Guid.NewGuid();
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.QuizGradeReopened,
            attempt.SessionId,
            attempt.Session.Sequence,
            participantId: attempt.ParticipantId,
            attemptId: attempt.Id,
            reason: request.Reason,
            eventId: eventId);
        await db.SaveChangesAsync(cancellationToken);
        var result = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        await audit.WriteAsync(
            "QuizGradeReopened",
            nameof(QuizAttempt),
            attempt.Id.ToString(),
            attempt.SessionId,
            before,
            new { ActorId = actorId, Grade = result, Reason = request.Reason.Trim() },
            cancellationToken);
        await EnqueueAsync(attempt, cancellationToken);
        await StoreReceiptAsync(
            request.MutationRequestId,
            attemptId,
            actorId,
            "ReopenQuizGrade",
            requestHash,
            result,
            eventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<StudentQuizReviewDto> GetStudentReviewAsync(
        Guid attemptId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        var attempt = await db.QuizAttemptsSet.AsNoTracking()
            .Include(x => x.Answers)
            .Include(x => x.Session)
            .FirstOrDefaultAsync(
                x => x.Id == attemptId && x.ParticipantId == participantId,
                cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài làm trắc nghiệm.", 404);
        EnsureFinalized(attempt);
        var returned = attempt.GradingStatus == GradingStatus.Returned
            && attempt.ReturnedAtUtc.HasValue;
        var scoreVisible = returned;
        return new(
            attempt.Id,
            scoreVisible ? attempt.Score : null,
            attempt.MaxScore,
            scoreVisible,
            returned,
            returned ? attempt.GeneralComment : null,
            await BuildQuestionsAsync(attempt, revealCorrect: returned, cancellationToken));
    }

    private async Task<QuizAttempt> RequireGradeableAttemptAsync(
        Guid attemptId,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        var actor = await RequireActorAsync(actorId, actorOrganizationId, cancellationToken);
        var attempt = await db.QuizAttemptsSet
            .Include(x => x.Answers)
            .Include(x => x.Participant)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(x => x.Id == attemptId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài làm trắc nghiệm.", 404);
        if (attempt.ParticipantId != attempt.Participant.Id
            || attempt.Participant.SessionId != attempt.SessionId
            || attempt.Session.ExamId != attempt.Session.Exam.Id
            || attempt.Session.DeliveryTypeSnapshot != ExamDeliveryType.MultipleChoice
            || attempt.Session.Exam.DeliveryType != ExamDeliveryType.MultipleChoice
            || attempt.ExamVersion != attempt.Session.ExamVersionSnapshot)
        {
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Quiz attempt không khớp participant/session/exam.", 409);
        }
        if (attempt.Session.AccessMode == SessionAccessMode.LanOnly
            && string.Equals(attempt.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(ErrorCodes.InvalidStateTransition, "PublicCloud attempt không được chấm qua OnlyLAN path.", 409);
        }
        if (attempt.Session.AccessMode == SessionAccessMode.PublicCloud
            && !string.Equals(attempt.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Attempt không thuộc PublicCloud session.", 409);
        }
        EnsureFinalized(attempt);
        if (!await CanAccessExamAsync(attempt.Session.Exam, actor, cancellationToken))
            throw new ApiException(ErrorCodes.Forbidden, "Không được chấm bài thuộc tổ chức khác.", 403);
        return attempt;
    }

    private async Task<User> RequireActorAsync(
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        var actor = await db.UsersSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actorId, cancellationToken);
        if (actor is null
            || !actor.IsActive
            || actor.Role is not (UserRole.Teacher or UserRole.Admin)
            || string.IsNullOrWhiteSpace(actorOrganizationId)
            || string.IsNullOrWhiteSpace(actor.OrganizationId)
            || !string.Equals(actor.OrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "Không được phép chấm Quiz hoặc tổ chức không hợp lệ.", 403);
        }
        return actor;
    }

    private async Task<bool> CanAccessExamAsync(
        Exam exam,
        User actor,
        CancellationToken cancellationToken)
    {
        if (!exam.CreatedBy.HasValue || string.IsNullOrWhiteSpace(actor.OrganizationId))
            return false;
        if (exam.CreatedBy.Value == actor.Id)
            return true;
        var ownerOrganization = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == exam.CreatedBy.Value && x.IsActive)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(ownerOrganization)
            && string.Equals(ownerOrganization, actor.OrganizationId, StringComparison.Ordinal);
    }

    private async Task<QuizGradeDetailDto> ToTeacherDtoAsync(
        QuizAttempt attempt,
        bool revealCorrect,
        CancellationToken cancellationToken) =>
        new(
            attempt.Id,
            attempt.SessionId,
            attempt.ParticipantId,
            attempt.Participant.StudentCode,
            attempt.Participant.DisplayName,
            attempt.Session.Exam.Title,
            attempt.AutoScore,
            attempt.Score,
            attempt.MaxScore,
            attempt.GradingStatus,
            attempt.GeneralComment,
            attempt.GraderId,
            attempt.GradedAtUtc,
            attempt.ReturnedAtUtc,
            attempt.Session.AccessMode == SessionAccessMode.PublicCloud
                ? attempt.CloudVersion.ToString(CultureInfo.InvariantCulture)
                : attempt.RowVersion,
            await BuildQuestionsAsync(attempt, revealCorrect, cancellationToken));

    private async Task<IReadOnlyList<QuizQuestionReviewDto>> BuildQuestionsAsync(
        QuizAttempt attempt,
        bool revealCorrect,
        CancellationToken cancellationToken)
    {
        var snapshot = QuizGradeAuthoritativeScoring.ParseSnapshot(attempt.SnapshotJson);
        var questionIds = snapshot.Select(x => x.Id).ToList();
        var correctByQuestion = await db.QuizChoicesSet.AsNoTracking()
            .Where(x => questionIds.Contains(x.QuestionId) && x.IsCorrect)
            .GroupBy(x => x.QuestionId)
            .ToDictionaryAsync(
                x => x.Key,
                x => x.Select(c => c.Id).ToHashSet(),
                cancellationToken);
        var selectedByQuestion = attempt.Answers.ToDictionary(
            x => x.QuestionId,
            x => (JsonSerializer.Deserialize<List<Guid>>(x.ChoiceIdsJson, Json) ?? []).ToHashSet());
        return snapshot.OrderBy(x => x.Order).Select(question =>
        {
            selectedByQuestion.TryGetValue(question.Id, out var selected);
            selected ??= [];
            correctByQuestion.TryGetValue(question.Id, out var correct);
            correct ??= [];
            var earned = revealCorrect
                ? (correct.SetEquals(selected) ? question.Points : 0m)
                : (decimal?)null;
            return new QuizQuestionReviewDto(
                question.Id,
                question.Text,
                question.Order,
                question.Points,
                earned,
                question.Choices.OrderBy(x => x.Order).Select(choice => new QuizChoiceReviewDto(
                    choice.Id,
                    choice.Text,
                    choice.Order,
                    selected.Contains(choice.Id),
                    revealCorrect ? correct.Contains(choice.Id) : null)).ToList());
        }).ToList();
    }

    private static void EnsureFinalized(QuizAttempt attempt)
    {
        if (attempt.Status != QuizAttemptStatus.Finalized)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ chấm bài đã finalize.", 409);
    }

    private static void EnsureConcurrency(QuizAttempt attempt, string rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)
            || !string.Equals(attempt.RowVersion, rowVersion, StringComparison.Ordinal))
            throw new ApiException(ErrorCodes.ConcurrencyConflict, "Bài chấm đã được cập nhật ở nơi khác.", 409);
    }

    private static long RequireCloudVersion(string rowVersion)
    {
        if (!long.TryParse(
                rowVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var cloudVersion)
            || cloudVersion < 1)
        {
            throw new ApiException(
                ErrorCodes.ConcurrencyConflict,
                "PublicCloud grading version không hợp lệ; hãy tải lại bài chấm.",
                409);
        }
        return cloudVersion;
    }

    private static Guid RequireMutationRequestId(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Thiếu MutationRequestId cho thao tác chấm PublicCloud.");
        return requestId;
    }

    private async Task<QuizGradeDetailDto?> FindReceiptAsync(
        Guid requestId,
        Guid attemptId,
        Guid actorId,
        string action,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
            return null;
        var receipt = await db.QuizGradeMutationReceiptsSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (receipt is null)
            return null;
        if (receipt.AttemptId != attemptId
            || receipt.ActorId != actorId
            || !string.Equals(receipt.Action, action, StringComparison.Ordinal)
            || !string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "MutationRequestId đã được dùng cho nội dung khác.");
        }
        return JsonSerializer.Deserialize<QuizGradeDetailDto>(receipt.ResultJson, Json)
            ?? throw new ApiException(ErrorCodes.InvalidStateTransition, "Biên nhận mutation Quiz không hợp lệ.", 500);
    }

    private async Task StoreReceiptAsync(
        Guid requestId,
        Guid attemptId,
        Guid actorId,
        string action,
        string requestHash,
        QuizGradeDetailDto result,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
            return;
        db.QuizGradeMutationReceiptsSet.Add(new QuizGradeMutationReceipt
        {
            Id = requestId,
            AttemptId = attemptId,
            ActorId = actorId,
            Action = action,
            RequestHash = requestHash,
            ResultJson = JsonSerializer.Serialize(result, Json),
            EventId = eventId,
            AttemptRowVersion = result.RowVersion
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string HashRequest(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, Json)))).ToLowerInvariant();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ApplyCloudMutation(
        QuizAttempt attempt,
        CloudQuizGradeMutationResult result)
    {
        if (result.AttemptId != attempt.Id
            || result.SessionId != attempt.SessionId
            || result.ParticipantId != attempt.ParticipantId
            || result.CloudVersion < 1
            || result.MaxScore != 10.00m)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Supabase trả contract chấm PublicCloud không khớp bài đang mở.",
                502);
        }

        attempt.AutoScore = result.AutoScore;
        attempt.Score = result.Score;
        attempt.MaxScore = result.MaxScore;
        attempt.GradingStatus = result.Status;
        attempt.GeneralComment = result.GeneralComment;
        attempt.GraderId = result.GraderId;
        attempt.GradedAtUtc = result.GradedAtUtc;
        attempt.ReturnedAtUtc = result.ReturnedAtUtc;
        attempt.CloudVersion = result.CloudVersion;
        attempt.CloudUpdatedAtUtc = result.UpdatedAtUtc;
        attempt.CloudSyncState = "Pulled";
    }

    private ICloudAdapter RequireCloud() =>
        cloud ?? throw new ApiException(
            ErrorCodes.CloudOffline,
            "PublicCloud chưa được cấu hình cho thao tác chấm của giáo viên.",
            503);

    private Task EnqueueAsync(QuizAttempt attempt, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_attempts",
            attempt.Id.ToString(),
            "upsert",
            new
            {
                id = attempt.Id,
                session_id = attempt.SessionId,
                participant_id = attempt.ParticipantId,
                exam_version = attempt.ExamVersion,
                result_policy = attempt.ResultPolicySnapshot.ToString(),
                status = attempt.Status.ToString(),
                started_at = attempt.StartedAtUtc,
                deadline_at = attempt.DeadlineUtc,
                finalized_at = attempt.FinalizedAtUtc,
                auto_score = attempt.AutoScore,
                score = attempt.Score,
                max_score = attempt.MaxScore,
                grading_status = attempt.GradingStatus.ToString(),
                general_comment = attempt.GeneralComment,
                grader_id = attempt.GraderId,
                graded_at = attempt.GradedAtUtc,
                returned_at = attempt.ReturnedAtUtc,
                snapshot_json = attempt.SnapshotJson,
                finalize_idempotency_key = attempt.FinalizeIdempotencyKey,
                created_at = attempt.CreatedAtUtc,
                updated_at = attempt.UpdatedAtUtc
            },
            cancellationToken: cancellationToken);
}
