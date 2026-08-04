using System.Collections.Concurrent;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace ExamTransfer.Infrastructure.Services;

public sealed class ExamService(AppDbContext db, IStoragePaths paths, IChunkStorage chunks, IAuditService audit, IOutboxService outbox, IRealtimePublisher realtime, IOptions<ExamTransferOptions> options, ILogger<ExamService> logger, IHttpContextAccessor? httpContextAccessor = null) : IExamService
{
    private readonly ExamTransferOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> UploadGates = new();

    public async Task<PagedResult<ExamSummaryDto>> ListAsync(string? search, ExamStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.ExamsSet.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Title.Contains(term) || x.Subject.Contains(term));
        }
        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        else
            query = query.Where(x => x.Status != ExamStatus.Archived);

        var total = await query.CountAsync(cancellationToken);

        // SQLite stores DateTimeOffset as TEXT and EF Core cannot translate
        // ORDER BY for DateTimeOffset. Read only the lightweight sort keys,
        // order them in memory, then fetch the requested page and preserve
        // that order. This keeps the existing database format unchanged.
        var sortKeys = await query
            .Select(x => new { x.Id, x.UpdatedAtUtc })
            .ToListAsync(cancellationToken);
        var pageIds = sortKeys
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToList();

        if (pageIds.Count == 0)
            return new([], page, pageSize, total);

        var position = pageIds
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index);
        var rows = await query
            .Where(x => pageIds.Contains(x.Id))
            .Select(x => new
            {
                Entity = x,
                FileCount = x.Files.Count(f =>
                    f.TransferStatus == TransferStatus.Completed
                    && f.Version == x.Version),
                HasCommittedQuizSource = x.QuizImportSources.Any(source =>
                    source.ExamVersion == x.Version && source.Status == "Committed"),
                QuizQuestionCount = x.QuizQuestions.Count(question => question.Version == x.Version)
            })
            .ToListAsync(cancellationToken);
        var items = rows
            .OrderBy(x => position[x.Entity.Id])
            .Select(x => x.Entity.ToSummary(x.FileCount, x.HasCommittedQuizSource, x.QuizQuestionCount))
            .ToList();

        return new(items, page, pageSize, total);
    }

    public async Task<ExamDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var exam = await db.ExamsSet.AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.QuizImportSources)
            .Include(x => x.QuizQuestions)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
        return ToDetail(exam);
    }

    public async Task<ExamDetailDto> CreateAsync(CreateExamRequest request, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(async () =>
        {
            Validate(request.Title, request.Subject, request.DurationMinutes, request.FileRule);
            var policy = ValidateExamPolicy(request.DeliveryType, request.QuizResultPolicy, request.SupervisionMode);
            if (request.ClassId.HasValue && !await db.ClassesSet.AnyAsync(x => x.Id == request.ClassId, cancellationToken))
                throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);
            var exam = new Exam
            {
                ClassId = request.ClassId, Title = request.Title.Trim(), Subject = request.Subject.Trim(), Description = request.Description?.Trim(),
                DurationMinutes = request.DurationMinutes, DeliveryType = request.DeliveryType,
                QuizResultPolicy = policy.ResultPolicy, SupervisionMode = policy.SupervisionMode,
                FileRuleJson = JsonSerializer.Serialize(request.FileRule, JsonOptions), Status = ExamStatus.Draft, Version = 1,
                CreatedBy = RequestActorId()
            };
            db.ExamsSet.Add(exam); await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("ExamCreated", nameof(Exam), exam.Id.ToString(), null, null, ToAudit(exam), cancellationToken);
            await outbox.EnqueueAsync("exams", exam.Id.ToString(), "upsert", ToCloud(exam), cancellationToken: cancellationToken);
            return ToDetail(exam);
        }, cancellationToken);
    }

    public async Task<ExamDetailDto> UpdateAsync(Guid id, UpdateExamRequest request, CancellationToken cancellationToken)
    {
        var detail = await InTransactionAsync(async () =>
        {
            Validate(request.Title, request.Subject, request.DurationMinutes, request.FileRule);
            var policy = ValidateExamPolicy(request.DeliveryType, request.QuizResultPolicy, request.SupervisionMode);
            var exam = await db.ExamsSet.Include(x => x.Files).Include(x => x.QuizQuestions).Include(x => x.QuizImportSources).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
            EnsureRowVersion(exam.RowVersion, request.RowVersion);
            if (exam.Status == ExamStatus.Archived) throw new ApiException(ErrorCodes.InvalidStateTransition, "Không thể sửa bài kiểm tra đã lưu trữ.", 409);
            if (request.ClassId.HasValue && !await db.ClassesSet.AnyAsync(x => x.Id == request.ClassId.Value, cancellationToken))
                throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp.", 404);
            var hasSession = await db.ExamSessionsSet.AnyAsync(x => x.ExamId == exam.Id, cancellationToken);
            var hasAttempt = await db.QuizAttemptsSet.AnyAsync(x => x.Session.ExamId == exam.Id, cancellationToken);
            if ((exam.Status == ExamStatus.Published || hasSession || hasAttempt)
                && exam.DurationMinutes != request.DurationMinutes)
                throw new ApiException(
                    ErrorCodes.ExamDurationImmutable,
                    "Không thể đổi thời lượng sau khi phát hành/có phiên/có attempt; hãy nhân bản bài kiểm tra để đặt thời lượng mới.",
                    409);
            if ((exam.Status == ExamStatus.Published || hasSession || hasAttempt)
                && (exam.DeliveryType != request.DeliveryType
                    || exam.QuizResultPolicy != policy.ResultPolicy
                    || exam.SupervisionMode != policy.SupervisionMode))
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Không thể đổi loại bài, chính sách điểm hoặc giám sát sau khi phát hành/có phiên/có attempt; hãy nhân bản bài kiểm tra.", 409);
            var before = new { exam.Title, exam.Subject, exam.Description, exam.DurationMinutes, exam.FileRuleJson, exam.Version };
            exam.ClassId = request.ClassId; exam.Title = request.Title.Trim(); exam.Subject = request.Subject.Trim(); exam.Description = request.Description?.Trim();
            exam.DurationMinutes = request.DurationMinutes; exam.FileRuleJson = JsonSerializer.Serialize(request.FileRule, JsonOptions);
            exam.DeliveryType = request.DeliveryType;
            exam.QuizResultPolicy = policy.ResultPolicy;
            exam.SupervisionMode = policy.SupervisionMode;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("ExamUpdated", nameof(Exam), exam.Id.ToString(), null, before, ToAudit(exam), cancellationToken);
            await outbox.EnqueueAsync("exams", exam.Id.ToString(), "upsert", ToCloud(exam), cancellationToken: cancellationToken);
            return ToDetail(exam);
        }, cancellationToken);
        await NotifyActiveSessionsAsync(detail.Id, detail.Version, RealtimeEvents.ExamUpdated, cancellationToken);
        return detail;
    }

    public async Task<ExamDetailDto> PublishAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await InTransactionAsync(async () =>
        {
            var exam = await db.ExamsSet.Include(x => x.Files).Include(x => x.QuizQuestions).Include(x => x.QuizImportSources).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
            if (exam.Status is ExamStatus.Archived or ExamStatus.Cancelled) throw new ApiException(ErrorCodes.InvalidStateTransition, "Không thể phát hành bài kiểm tra ở trạng thái hiện tại.", 409);
            var rule = exam.ParseFileRule();
            var completed = exam.Files.Where(x => x.Version == exam.Version && x.TransferStatus == TransferStatus.Completed).ToList();
            if (exam.DeliveryType == ExamDeliveryType.FileSubmission && rule.RequireAtLeastOneFile && completed.Count == 0) throw new ApiException(ErrorCodes.ValidationFailed, "Bài kiểm tra yêu cầu ít nhất một file đề.", 422);
            if (exam.DeliveryType == ExamDeliveryType.MultipleChoice
                && (exam.SupervisionMode != SupervisionMode.Standard
                    || !exam.QuizQuestions.Any(x => x.Version == exam.Version)
                    || !exam.QuizImportSources.Any(x => x.ExamVersion == exam.Version && x.Status == "Committed")))
                throw new ApiException(ErrorCodes.ValidationFailed, "Đề trắc nghiệm phải có giám sát chuẩn, nguồn DOCX/PDF đã commit và ít nhất một câu hỏi.", 422);
            exam.Status = ExamStatus.Published;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("ExamPublished", nameof(Exam), exam.Id.ToString(), null, null, ToAudit(exam), cancellationToken);
            await outbox.EnqueueAsync("exams", exam.Id.ToString(), "upsert", ToCloud(exam), cancellationToken: cancellationToken);
            return ToDetail(exam);
        }, cancellationToken);
        await NotifyActiveSessionsAsync(detail.Id, detail.Version, RealtimeEvents.ExamPublished, cancellationToken);
        return detail;
    }

    public async Task ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = await BulkArchiveAsync(new([id]), cancellationToken);
    }

    public Task<BulkArchiveResultDto> BulkArchiveAsync(
        BulkArchiveRequest request,
        CancellationToken cancellationToken)
    {
        var ids = BulkArchiveValidation.Validate(request);
        return InTransactionAsync(async () =>
        {
            var exams = await db.ExamsSet
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
            var found = exams.Select(x => x.Id).ToHashSet();
            var missing = ids.Where(id => !found.Contains(id)).ToList();
            if (missing.Count > 0)
                throw new ApiException(
                    ErrorCodes.NotFound,
                    "Một hoặc nhiều bài kiểm tra không tồn tại.",
                    404,
                    details: new { missingIds = missing });

            var alreadyArchived = exams
                .Where(x => x.Status == ExamStatus.Archived)
                .Select(x => x.Id)
                .Order()
                .ToList();
            var toArchive = exams
                .Where(x => x.Status != ExamStatus.Archived)
                .OrderBy(x => x.Id)
                .ToList();
            var archiveIds = toArchive.Select(x => x.Id).ToList();
            var blockingSessions = await db.ExamSessionsSet
                .AsNoTracking()
                .Where(x => archiveIds.Contains(x.ExamId)
                    && x.Status != SessionStatus.Finished
                    && x.Status != SessionStatus.Cancelled
                    && x.Status != SessionStatus.Archived)
                .Select(x => new { x.Id, x.ExamId, x.RoomCode, x.Status })
                .ToListAsync(cancellationToken);
            if (blockingSessions.Count > 0)
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Không thể lưu trữ bài kiểm tra đang được một phiên chưa kết thúc sử dụng.",
                    409,
                    details: new { sessions = blockingSessions });

            var beforeStatuses = toArchive.ToDictionary(x => x.Id, x => x.Status);
            foreach (var exam in toArchive)
                exam.Status = ExamStatus.Archived;
            await db.SaveChangesAsync(cancellationToken);

            foreach (var exam in toArchive)
            {
                await audit.WriteAsync(
                    "ExamArchived",
                    nameof(Exam),
                    exam.Id.ToString(),
                    null,
                    new { status = beforeStatuses[exam.Id] },
                    ToAudit(exam),
                    cancellationToken);
                await outbox.EnqueueAsync(
                    "exams",
                    exam.Id.ToString(),
                    "upsert",
                    ToCloud(exam),
                    cancellationToken: cancellationToken);
            }

            return new BulkArchiveResultDto(
                ids.Count,
                toArchive.Count,
                alreadyArchived,
                []);
        }, cancellationToken);
    }

    public async Task<ExamDetailDto> CloneAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.ExamsSet.AsNoTracking().Include(x => x.Files).Include(x => x.QuizImportSources).Include(x => x.QuizQuestions).ThenInclude(x => x.Choices).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
        var clone = new Exam { ClassId = source.ClassId, Title = source.Title + " - Bản sao", Subject = source.Subject, Description = source.Description, DurationMinutes = source.DurationMinutes, DeliveryType = source.DeliveryType, QuizResultPolicy = source.QuizResultPolicy, SupervisionMode = source.SupervisionMode, FileRuleJson = source.FileRuleJson, Status = ExamStatus.Draft, Version = 1 };
        var sourceQuestions = source.QuizQuestions
            .Where(x => x.Version == source.Version)
            .OrderBy(x => x.Order)
            .ToList();
        var normalizedPoints = sourceQuestions.Count == 0
            ? []
            : QuizScoreAllocator.Allocate(sourceQuestions.Count);
        for (var questionIndex = 0; questionIndex < sourceQuestions.Count; questionIndex++)
        {
            var sourceQuestion = sourceQuestions[questionIndex];
            var question = new QuizQuestion { Version = 1, Order = sourceQuestion.Order, Text = sourceQuestion.Text, Points = normalizedPoints[questionIndex], Multiple = sourceQuestion.Multiple };
            foreach (var choice in sourceQuestion.Choices.OrderBy(x => x.Order)) question.Choices.Add(new QuizChoice { Order = choice.Order, Text = choice.Text, IsCorrect = choice.IsCorrect });
            clone.QuizQuestions.Add(question);
        }
        foreach (var sourceDocument in source.QuizImportSources.Where(x => x.ExamVersion == source.Version && x.Status == "Committed"))
        {
            var sourcePath = Path.GetFullPath(Path.Combine(paths.RootPath, sourceDocument.RelativePath));
            EnsureInsideRoot(sourcePath);
            if (!File.Exists(sourcePath))
                throw new ApiException(ErrorCodes.NotFound, "File nguồn trắc nghiệm không tồn tại; không thể nhân bản an toàn.", 409);
            var destinationRoot = Path.Combine(paths.ExamVersionRoot(clone.Id, 1), "quiz-source");
            Directory.CreateDirectory(destinationRoot);
            var destinationPath = Path.Combine(destinationRoot, $"{Guid.NewGuid():N}{Path.GetExtension(sourceDocument.OriginalName).ToLowerInvariant()}");
            await CopyFileAsync(sourcePath, destinationPath, cancellationToken);
            clone.QuizImportSources.Add(new QuizImportSource
            {
                ExamVersion = 1,
                OriginalName = sourceDocument.OriginalName,
                MimeType = sourceDocument.MimeType,
                SizeBytes = sourceDocument.SizeBytes,
                Sha256 = sourceDocument.Sha256,
                RelativePath = Path.GetRelativePath(paths.RootPath, destinationPath),
                Status = "Committed",
                CreatedBy = sourceDocument.CreatedBy,
                ImportedAtUtc = DateTimeOffset.UtcNow
            });
        }
        db.ExamsSet.Add(clone);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "ExamCloned",
            nameof(Exam),
            clone.Id.ToString(),
            null,
            new { sourceId = source.Id },
            ToAudit(clone),
            cancellationToken);
        await outbox.EnqueueAsync(
            "exams",
            clone.Id.ToString(),
            "upsert",
            ToCloud(clone),
            cancellationToken: cancellationToken);
        foreach (var question in clone.QuizQuestions.OrderBy(x => x.Order))
        {
            await outbox.EnqueueAsync(
                "quiz_questions",
                question.Id.ToString(),
                "upsert",
                QuizQuestionToCloud(question),
                cancellationToken: cancellationToken);
            foreach (var choice in question.Choices.OrderBy(x => x.Order))
                await outbox.EnqueueAsync(
                    "quiz_choices",
                    choice.Id.ToString(),
                    "upsert",
                    QuizChoiceToCloud(choice),
                    cancellationToken: cancellationToken);
        }
        foreach (var sourceDocument in clone.QuizImportSources
                     .Where(x => x.ExamVersion == clone.Version && x.Status == "Committed")
                     .OrderBy(x => x.Id))
        {
            var sourcePath = Path.GetFullPath(Path.Combine(paths.RootPath, sourceDocument.RelativePath));
            EnsureInsideRoot(sourcePath);
            await outbox.EnqueueAsync(
                "quiz_import_sources",
                sourceDocument.Id.ToString(),
                "upsert",
                QuizSourceToCloud(sourceDocument),
                sourcePath,
                cancellationToken);
        }
        return ToDetail(clone);
    }

    public async Task<InitFileUploadResponse> InitFileAsync(Guid examId, InitFileUploadRequest request, CancellationToken cancellationToken)
    {
        var gate = UploadGates.GetOrAdd(examId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            var exam = await db.ExamsSet
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == examId, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);

            if (exam.Status is ExamStatus.Archived or ExamStatus.Cancelled)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Không thể thêm file vào bài kiểm tra ở trạng thái hiện tại.", 409);

            var normalizedName = Path.GetFileName(request.FileName);
            ValidateFile(normalizedName, request.SizeBytes, request.Sha256);

            var copiedFiles = new List<(ExamFile Entity, string FullPath)>();
            var persisted = false;
            var previousVersion = exam.Version;
            var createdNewVersion = exam.Status == ExamStatus.Published;

            try
            {
                if (createdNewVersion)
                {
                    copiedFiles = await PrepareDraftVersionAsync(
                        exam,
                        normalizedName,
                        cancellationToken);
                }

                var chunkSize = Math.Clamp(
                    request.ChunkSizeBytes ?? _options.Transfer.ChunkSizeBytes,
                    1024 * 1024,
                    _options.Transfer.MaxChunkSizeBytes);

                var fileId = Guid.NewGuid();
                var storedName = fileId.ToString("N") + Path.GetExtension(normalizedName).ToLowerInvariant();
                var transferRoot = Path.Combine(
                    paths.TemporaryRoot,
                    "exams",
                    examId.ToString("N"),
                    fileId.ToString("N"),
                    "chunks");

                var entity = new ExamFile
                {
                    Id = fileId,
                    ExamId = examId,
                    Exam = exam,
                    Version = exam.Version,
                    OriginalName = normalizedName,
                    StoredName = storedName,
                    MimeType = request.MimeType,
                    SizeBytes = request.SizeBytes,
                    Sha256 = request.Sha256.ToLowerInvariant(),
                    ChunkSizeBytes = chunkSize,
                    TotalChunks = (int)Math.Ceiling(request.SizeBytes / (double)chunkSize),
                    TemporaryPath = transferRoot,
                    TransferStatus = TransferStatus.Running
                };

                db.ExamFilesSet.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                persisted = true;

                if (createdNewVersion)
                {
                    await audit.WriteAsync(
                        "ExamDraftVersionCreated",
                        nameof(Exam),
                        exam.Id.ToString(),
                        null,
                        new { version = previousVersion, status = ExamStatus.Published.ToString() },
                        new { version = exam.Version, status = exam.Status.ToString(), copiedFileCount = copiedFiles.Count },
                        cancellationToken);

                    await outbox.EnqueueAsync(
                        "exams",
                        exam.Id.ToString(),
                        "upsert",
                        ToCloud(exam),
                        cancellationToken: cancellationToken);

                    foreach (var copied in copiedFiles)
                    {
                        await outbox.EnqueueAsync(
                            "exam_files",
                            copied.Entity.Id.ToString(),
                            "upsert",
                            ToCloud(copied.Entity),
                            copied.FullPath,
                            cancellationToken);
                    }
                }

                var missing = Enumerable.Range(0, entity.TotalChunks).ToList();
                return new InitFileUploadResponse(entity.Id, chunkSize, entity.TotalChunks, missing);
            }
            catch
            {
                if (!persisted)
                {
                    foreach (var copied in copiedFiles)
                    {
                        TryDeleteFile(copied.FullPath);
                    }
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UploadChunkAsync(Guid examId, Guid fileId, int index, Stream content, long contentLength, string? chunkSha256, CancellationToken cancellationToken)
    {
        var file = await db.ExamFilesSet.FirstOrDefaultAsync(x => x.Id == fileId && x.ExamId == examId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file upload.", 404);
        if (index < 0 || index >= file.TotalChunks) throw new ApiException(ErrorCodes.ChunkMismatch, "Chỉ số chunk nằm ngoài phạm vi.");
        if (contentLength <= 0 || contentLength > file.ChunkSizeBytes) throw new ApiException(ErrorCodes.ChunkMismatch, "Kích thước chunk không hợp lệ.");
        await chunks.WriteChunkAsync(file.TemporaryPath, index, content, file.ChunkSizeBytes, chunkSha256, cancellationToken);
        var received = chunks.ReadReceivedChunks(file.ReceivedChunksJson).ToHashSet(); received.Add(index); file.ReceivedChunksJson = chunks.WriteReceivedChunks(received); file.TransferStatus = TransferStatus.Running;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<FileDescriptorDto> FinalizeFileAsync(Guid examId, Guid fileId, FinalizeFileUploadRequest request, CancellationToken cancellationToken)
    {
        var file = await db.ExamFilesSet.Include(x => x.Exam).FirstOrDefaultAsync(x => x.Id == fileId && x.ExamId == examId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file upload.", 404);
        if (file.TransferStatus == TransferStatus.Completed) return ToFileDto(file);
        if (!file.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase)) throw new ApiException(ErrorCodes.HashMismatch, "Hash finalize không khớp metadata init.");
        var root = paths.ExamVersionRoot(examId, file.Version); Directory.CreateDirectory(root);
        var finalPath = Path.Combine(root, file.StoredName);
        await chunks.AssembleAndVerifyAsync(file.TemporaryPath, file.TotalChunks, file.SizeBytes, file.Sha256, finalPath, cancellationToken);
        file.RelativePath = Path.GetRelativePath(paths.RootPath, finalPath);
        file.TransferStatus = TransferStatus.Completed;

        var superseded = await db.ExamFilesSet
            .Where(x => x.ExamId == examId
                && x.Version == file.Version
                && x.Id != file.Id
                && x.TransferStatus == TransferStatus.Completed
                && x.OriginalName.ToLower() == file.OriginalName.ToLower())
            .ToListAsync(cancellationToken);

        db.ExamFilesSet.RemoveRange(superseded);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var oldFile in superseded)
        {
            var oldPath = string.IsNullOrWhiteSpace(oldFile.RelativePath)
                ? null
                : Path.GetFullPath(Path.Combine(paths.RootPath, oldFile.RelativePath));

            if (oldPath is not null)
            {
                EnsureInsideRoot(oldPath);
                TryDeleteFile(oldPath);
            }

            await outbox.EnqueueAsync(
                "exam_files",
                oldFile.Id.ToString(),
                "delete",
                new
                {
                    id = oldFile.Id,
                    name = oldFile.OriginalName,
                    cloud_object_path = oldFile.CloudObjectPath
                },
                cancellationToken: cancellationToken);
        }

        await audit.WriteAsync("ExamFileFinalized", nameof(ExamFile), file.Id.ToString(), null, null, new { file.ExamId, file.OriginalName, file.SizeBytes, file.Sha256, file.Version }, cancellationToken);
        await outbox.EnqueueAsync(
            "exam_files",
            file.Id.ToString(),
            "upsert",
            ToCloud(file),
            finalPath,
            cancellationToken);
        return ToFileDto(file);
    }

    public async Task DeleteFileAsync(Guid examId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.ExamFilesSet.Include(x => x.Exam).FirstOrDefaultAsync(x => x.Id == fileId && x.ExamId == examId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file.", 404);
        if (file.Exam.Status == ExamStatus.Published && file.Version == file.Exam.Version) throw new ApiException(ErrorCodes.InvalidStateTransition, "Không xóa trực tiếp file của phiên bản đã phát hành; hãy tạo phiên bản mới.", 409);
        var full = string.IsNullOrWhiteSpace(file.RelativePath) ? null : Path.Combine(paths.RootPath, file.RelativePath);
        db.ExamFilesSet.Remove(file); await db.SaveChangesAsync(cancellationToken);
        if (full is not null && File.Exists(full)) File.Delete(full);
        await audit.WriteAsync("ExamFileDeleted", nameof(ExamFile), file.Id.ToString(), null, ToAudit(file), null, cancellationToken);
        await outbox.EnqueueAsync(
            "exam_files",
            file.Id.ToString(),
            "delete",
            new
            {
                id = file.Id,
                name = file.OriginalName,
                cloud_object_path = file.CloudObjectPath
            },
            cancellationToken: cancellationToken);
    }

    public async Task<ExamManifestDto> GetManifestAsync(Guid examId, CancellationToken cancellationToken)
    {
        var exam = await db.ExamsSet.AsNoTracking().Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == examId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
        var files = exam.Files.Where(x => x.Version == exam.Version && x.TransferStatus == TransferStatus.Completed).OrderBy(x => x.OriginalName).Select(ToFileDto).ToList();
        return new ExamManifestDto(exam.Id, exam.Version, DateTimeOffset.UtcNow, files);
    }

    public async Task<(string Path, string MimeType, string DownloadName)> GetFileContentAsync(Guid examId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.ExamFilesSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.ExamId == examId && x.TransferStatus == TransferStatus.Completed, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file.", 404);
        var full = Path.GetFullPath(Path.Combine(paths.RootPath, file.RelativePath)); EnsureInsideRoot(full);
        if (!File.Exists(full)) throw new ApiException(ErrorCodes.NotFound, "File vật lý không tồn tại.", 404);
        return (full, file.MimeType, file.OriginalName);
    }

    private async Task<List<(ExamFile Entity, string FullPath)>> PrepareDraftVersionAsync(
        Exam exam,
        string replacementName,
        CancellationToken cancellationToken)
    {
        var sourceVersion = exam.Version;
        var targetVersion = sourceVersion + 1;
        var copied = new List<(ExamFile Entity, string FullPath)>();
        var targetRoot = paths.ExamVersionRoot(exam.Id, targetVersion);
        Directory.CreateDirectory(targetRoot);

        foreach (var source in exam.Files
            .Where(x => x.Version == sourceVersion
                && x.TransferStatus == TransferStatus.Completed
                && !x.OriginalName.Equals(replacementName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.OriginalName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = Path.GetFullPath(Path.Combine(paths.RootPath, source.RelativePath));
            EnsureInsideRoot(sourcePath);
            if (!File.Exists(sourcePath))
                throw new ApiException(ErrorCodes.NotFound, $"File nguồn {source.OriginalName} không tồn tại; không thể tạo phiên bản đề mới.", 409);

            var newId = Guid.NewGuid();
            var storedName = newId.ToString("N") + Path.GetExtension(source.OriginalName).ToLowerInvariant();
            var targetPath = Path.Combine(targetRoot, storedName);

            await CopyFileAsync(sourcePath, targetPath, cancellationToken);

            var entity = new ExamFile
            {
                Id = newId,
                ExamId = exam.Id,
                Exam = exam,
                Version = targetVersion,
                OriginalName = source.OriginalName,
                StoredName = storedName,
                RelativePath = Path.GetRelativePath(paths.RootPath, targetPath),
                TemporaryPath = string.Empty,
                MimeType = source.MimeType,
                SizeBytes = source.SizeBytes,
                Sha256 = source.Sha256,
                ChunkSizeBytes = source.ChunkSizeBytes,
                TotalChunks = source.TotalChunks,
                ReceivedChunksJson = source.ReceivedChunksJson,
                TransferStatus = TransferStatus.Completed,
                SyncStatus = SyncStatus.LocalOnly
            };

            db.ExamFilesSet.Add(entity);
            copied.Add((entity, targetPath));
        }

        exam.Version = targetVersion;
        exam.Status = ExamStatus.Draft;
        return copied;
    }

    private async Task NotifyActiveSessionsAsync(
        Guid examId,
        int version,
        string eventName,
        CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            SessionStatus.Waiting,
            SessionStatus.Distributing,
            SessionStatus.InProgress,
            SessionStatus.Paused,
            SessionStatus.Collecting
        };

        var sessions = await db.ExamSessionsSet
            .Where(x => x.ExamId == examId && activeStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
            session.Sequence++;

        await db.SaveChangesAsync(cancellationToken);

        foreach (var session in sessions)
        {
            try
            {
                await realtime.PublishSessionAsync(
                    session.Id,
                    eventName,
                    session.Sequence,
                    new
                    {
                        examId,
                        version,
                        manifestUrl = $"/api/v1/exams/{examId}/manifest"
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime publish failed after local exam commit. ExamId={ExamId}; SessionId={SessionId}; Event={EventName}", examId, session.Id, eventName);
            }
        }
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateAggregateFileRules(Exam exam, string incomingName, long incomingSize, FileRuleDto rule)
    {
        var relevant = exam.Files
            .Where(x => x.Version == exam.Version
                && x.TransferStatus is not TransferStatus.Failed and not TransferStatus.Cancelled
                && !x.OriginalName.Equals(incomingName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relevant.Count + 1 > rule.MaxFileCount)
            throw new ApiException(ErrorCodes.ValidationFailed, $"Số lượng file vượt giới hạn {rule.MaxFileCount} file.");

        var totalSize = relevant.Sum(x => x.SizeBytes) + incomingSize;
        if (totalSize > rule.MaxTotalSizeBytes)
            throw new ApiException(ErrorCodes.FileTooLarge, "Tổng dung lượng file đề vượt giới hạn cho phép.");
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, bufferSize, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort; the database remains the source of truth.
        }
    }

    private ExamDetailDto ToDetail(Exam exam)
    {
        var source = exam.QuizImportSources
            .Where(x => x.ExamVersion == exam.Version && x.Status == "Committed")
            .OrderByDescending(x => x.ImportedAtUtc)
            .Select(x => new QuizImportSourceDto(x.Id, x.OriginalName, x.MimeType, x.SizeBytes,
                x.Sha256, x.ExamVersion, x.Status, x.ImportedAtUtc))
            .FirstOrDefault();
        return exam.ToDetail(
            exam.Files.Where(x => x.Version == exam.Version && x.TransferStatus == TransferStatus.Completed).Select(ToFileDto).ToList(),
            source,
            exam.QuizQuestions.Count(x => x.Version == exam.Version));
    }
    private static FileDescriptorDto ToFileDto(ExamFile x) => new(x.Id, x.OriginalName, x.SizeBytes, x.Sha256, x.MimeType, $"/api/v1/exams/{x.ExamId}/files/{x.Id}/content");
    private static object ToAudit(Exam x) => new
    {
        id = x.Id,
        class_id = x.ClassId,
        title = x.Title,
        subject = x.Subject,
        description = x.Description,
        duration_minutes = x.DurationMinutes,
        delivery_type = x.DeliveryType.ToString(),
        quiz_result_policy = x.QuizResultPolicy.ToString(),
        supervision_mode = x.SupervisionMode.ToString(),
        file_rule_json = x.FileRuleJson,
        status = x.Status.ToString(),
        version = x.Version,
        created_by = x.CreatedBy,
        completed_file_count = x.Files.Count(file =>
            file.Version == x.Version
            && file.TransferStatus == TransferStatus.Completed),
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc,
        row_version = x.RowVersion
    };

    private static object ToAudit(ExamFile x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        version = x.Version,
        original_name = x.OriginalName,
        stored_name = x.StoredName,
        relative_path = x.RelativePath,
        mime_type = x.MimeType,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        transfer_status = x.TransferStatus.ToString(),
        sync_status = x.SyncStatus.ToString(),
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc,
        row_version = x.RowVersion
    };
    private static object ToCloud(Exam x) => new
    {
        id = x.Id,
        class_id = x.ClassId,
        title = x.Title,
        subject = x.Subject,
        description = x.Description,
        duration_minutes = x.DurationMinutes,
        delivery_type = x.DeliveryType.ToString(),
        quiz_result_policy = x.QuizResultPolicy.ToString(),
        supervision_mode = x.SupervisionMode.ToString(),
        file_rule_json = x.FileRuleJson,
        status = x.Status.ToString(),
        version = x.Version,
        created_by = x.CreatedBy,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object QuizQuestionToCloud(QuizQuestion x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        version = x.Version,
        sort_order = x.Order,
        question_text = x.Text,
        points = x.Points,
        multiple = x.Multiple,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object QuizChoiceToCloud(QuizChoice x) => new
    {
        id = x.Id,
        question_id = x.QuestionId,
        sort_order = x.Order,
        choice_text = x.Text,
        is_correct = x.IsCorrect,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object QuizSourceToCloud(QuizImportSource x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        exam_version = x.ExamVersion,
        original_name = x.OriginalName,
        mime_type = x.MimeType,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        status = x.Status,
        created_by = x.CreatedBy,
        imported_at = x.ImportedAtUtc,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object ToCloud(ExamFile x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        version = x.Version,
        name = x.OriginalName,
        stored_name = x.StoredName,
        mime_type = x.MimeType,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        transfer_status = x.TransferStatus.ToString(),
        sync_status = x.SyncStatus.ToString(),
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };
    private Guid? RequestActorId() =>
        Guid.TryParse(
            httpContextAccessor?.HttpContext?.User.FindFirstValue("provider_user_id")
                ?? httpContextAccessor?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContextAccessor?.HttpContext?.User.FindFirstValue("sub"),
            out var actorId)
            && actorId != Guid.Empty
                ? actorId
                : null;
    private void EnsureInsideRoot(string path) { if (!path.StartsWith(Path.GetFullPath(paths.RootPath), StringComparison.OrdinalIgnoreCase)) throw new ApiException(ErrorCodes.Forbidden, "Đường dẫn file không hợp lệ.", 403); }
    private static void Validate(string title, string subject, int duration, FileRuleDto rule)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(subject) || duration <= 0) throw new ApiException(ErrorCodes.ValidationFailed, "Tiêu đề, môn học và thời lượng hợp lệ là bắt buộc.");
        if (rule.MaxFileSizeBytes <= 0 || rule.MaxTotalSizeBytes <= 0 || rule.MaxFileCount <= 0) throw new ApiException(ErrorCodes.ValidationFailed, "Quy tắc file không hợp lệ.");
    }
    private static (QuizResultPolicy ResultPolicy, SupervisionMode SupervisionMode) ValidateExamPolicy(
        ExamDeliveryType deliveryType,
        QuizResultPolicy resultPolicy,
        SupervisionMode supervisionMode)
    {
        if (!Enum.IsDefined(deliveryType) || !Enum.IsDefined(resultPolicy) || !Enum.IsDefined(supervisionMode))
            throw new ApiException(ErrorCodes.ValidationFailed, "Loại bài hoặc chính sách không hợp lệ.");
        if (deliveryType == ExamDeliveryType.MultipleChoice)
        {
            if (supervisionMode != SupervisionMode.Standard)
                throw new ApiException(ErrorCodes.ValidationFailed, "Trắc nghiệm bắt buộc dùng giám sát chuẩn.");
            return (resultPolicy, SupervisionMode.Standard);
        }
        return (QuizResultPolicy.Hidden, supervisionMode);
    }
    private static void ValidateFile(string name, long size, string sha)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name)
            throw new ApiException(ErrorCodes.ValidationFailed, "Tên file đề không hợp lệ.");
        if (size <= 0) throw new ApiException(ErrorCodes.ValidationFailed, "Kích thước file đề phải lớn hơn 0.");
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new ApiException(ErrorCodes.ValidationFailed, "SHA-256 không hợp lệ.");
    }
    private static void EnsureRowVersion(string current, string supplied) { if (current != supplied) throw new ApiException(ErrorCodes.ConcurrencyConflict, "Dữ liệu đã thay đổi.", 409, details: new { currentRowVersion = current }); }
}
