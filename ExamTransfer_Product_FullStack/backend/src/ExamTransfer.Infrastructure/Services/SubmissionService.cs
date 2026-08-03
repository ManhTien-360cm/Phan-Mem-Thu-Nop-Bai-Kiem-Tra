using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SubmissionService(AppDbContext db, IStoragePaths paths, IChunkStorage chunks, IReceiptSigner receipts, IAuditService audit, IOutboxService outbox, IRealtimePublisher realtime, IOptions<ExamTransferOptions> options, SubmissionMutationDispatcher submissionMutations) : ISubmissionService
{
    private readonly ExamTransferOptions _options = options.Value;
    private readonly SubmissionMutationDispatcher _submissionMutations = submissionMutations;

    public async Task<InitSubmissionResponse> InitAsync(InitSubmissionRequest request, CancellationToken cancellationToken)
    {
        if (request.Files.Count != StudentSubmissionPolicy.MaxFileCount)
            throw new ApiException(ErrorCodes.SubmissionFileCountInvalid, "Bài nộp phải có đúng một file nén.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ApiException(ErrorCodes.ValidationFailed, "Thiếu idempotencyKey.");
        ValidateFiles(request.Files);

        Submission? submission = null;
        SessionParticipant? participant = null;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            participant = await db.SessionParticipantsSet
                .Include(x => x.Session)
                .ThenInclude(x => x.Exam)
                .FirstOrDefaultAsync(
                    x => x.Id == request.ParticipantId
                        && x.SessionId == request.SessionId,
                    cancellationToken)
                ?? throw new ApiException(
                    ErrorCodes.NotFound,
                    "Không tìm thấy người tham gia.",
                    404);
            if (participant.Status != ParticipantStatus.Approved)
                throw new ApiException(ErrorCodes.Forbidden, "Người tham gia chưa được duyệt.", 403);
            EnsureSessionAcceptsSubmission(participant.Session.Status);

            var existing = await db.SubmissionsSet
                .Include(x => x.Files)
                .FirstOrDefaultAsync(
                    x => x.ParticipantId == request.ParticipantId
                        && x.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken);
            if (existing is not null)
            {
                EnsureIdempotencyMatches(existing, request);
                await transaction.CommitAsync(cancellationToken);
                return ToInitResponse(existing);
            }

            if (participant.Session.Exam.DeliveryType != ExamDeliveryType.FileSubmission)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài thi trắc nghiệm không sử dụng luồng nộp file.", 409);

            var previousAttempts = await db.SubmissionsSet
                .Where(x => x.ParticipantId == participant.Id)
                .ToListAsync(cancellationToken);
            if (previousAttempts.Any(x => SubmissionStatePolicy.IsActiveSubmissionStatus(x.Status)))
                throw new ApiException(
                    ErrorCodes.SubmissionAlreadyProcessing,
                    "Đã có một lần nộp bài đang được xử lý.",
                    409);
            if (previousAttempts.Any(x => SubmissionStatePolicy.IsCompletedSubmissionStatus(x.Status))
                && !participant.ResubmitAllowed)
            {
                throw new ApiException(
                    ErrorCodes.ResubmitNotAllowed,
                    "Đã có bài nộp; giáo viên chưa cho phép nộp lại.",
                    409);
            }

            var attempt = (previousAttempts.Count == 0
                ? 0
                : previousAttempts.Max(x => x.AttemptNumber)) + 1;
            var deadline = participant.Session.StartedAtUtc!.Value.AddMinutes(
                participant.Session.Exam.DurationMinutes + participant.ExtraTimeMinutes);
            submission = new Submission
            {
                SessionId = request.SessionId,
                ParticipantId = request.ParticipantId,
                AttemptNumber = attempt,
                IdempotencyKey = request.IdempotencyKey,
                Status = SubmissionStatus.Uploading,
                ClientSubmittedAtUtc = request.ClientSubmittedAtUtc,
                DeadlineUtc = deadline,
                IsOfficial = false
            };
            foreach (var input in request.Files)
            {
                var fileId = Guid.NewGuid();
                var storedName = fileId.ToString("N")
                    + Path.GetExtension(input.Name).ToLowerInvariant();
                var transferRoot = Path.Combine(
                    paths.SessionRoot(request.SessionId),
                    "temporary",
                    submission.Id.ToString("N"),
                    fileId.ToString("N"),
                    "chunks");
                submission.Files.Add(new SubmissionFile
                {
                    Id = fileId,
                    ClientFileId = input.ClientFileId,
                    OriginalName = Path.GetFileName(input.Name),
                    StoredName = storedName,
                    MimeType = input.MimeType,
                    SizeBytes = input.SizeBytes,
                    Sha256 = input.Sha256.ToLowerInvariant(),
                    ChunkSizeBytes = _options.Transfer.ChunkSizeBytes,
                    TotalChunks = (int)Math.Ceiling(
                        input.SizeBytes / (double)_options.Transfer.ChunkSizeBytes),
                    TemporaryPath = transferRoot,
                    TransferStatus = TransferStatus.Running
                });
            }

            db.SubmissionsSet.Add(submission);
            participant.SubmissionStatus = SubmissionStatus.Uploading;
            participant.ResubmitAllowed = false;
            participant.Session.Sequence++;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSubmissionUniquenessViolation(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return await ResolveConcurrentInitAsync(request, cancellationToken);
            }
        }

        await audit.WriteAsync("SubmissionStarted", nameof(Submission), submission.Id.ToString(), submission.SessionId, null, new { submission.ParticipantId, submission.AttemptNumber, fileCount = submission.Files.Count }, cancellationToken);
        await realtime.PublishSessionAsync(submission.SessionId, RealtimeEvents.SubmissionStarted, participant.Session.Sequence, new { submissionId = submission.Id, participantId = participant.Id, attempt = submission.AttemptNumber }, cancellationToken);
        return ToInitResponse(submission);
    }

    public async Task UploadChunkAsync(Guid submissionId, Guid fileId, int index, Stream content, long contentLength, string? chunkSha256, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var file = await db.SubmissionFilesSet
            .Include(x => x.Submission)
            .ThenInclude(x => x.Participant)
            .ThenInclude(x => x.Session)
            .FirstOrDefaultAsync(
                x => x.Id == fileId && x.SubmissionId == submissionId,
                cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.SubmissionNotFound,
                "Không tìm thấy file bài nộp.",
                404);
        EnsureSessionAcceptsSubmission(file.Submission.Participant.Session.Status);
        if (SubmissionStatePolicy.IsCompletedSubmissionStatus(file.Submission.Status))
            throw new ApiException(
                ErrorCodes.SubmissionAlreadyCompleted,
                "Bài nộp đã đóng.",
                409);
        if (!SubmissionStatePolicy.AcceptsChunks(file.Submission.Status))
            throw new ApiException(
                ErrorCodes.SubmissionAlreadyProcessing,
                "Bài nộp đang được hoàn tất.",
                409);
        if (index < 0 || index >= file.TotalChunks || contentLength <= 0 || contentLength > file.ChunkSizeBytes) throw new ApiException(ErrorCodes.ChunkMismatch, "Chunk không hợp lệ.");
        await chunks.WriteChunkAsync(file.TemporaryPath, index, content, file.ChunkSizeBytes, chunkSha256, cancellationToken);
        var received = chunks.ReadReceivedChunks(file.ReceivedChunksJson).ToHashSet(); received.Add(index); file.ReceivedChunksJson = chunks.WriteReceivedChunks(received); file.TransferStatus = TransferStatus.Running;
        file.Submission.Status = SubmissionStatus.Uploading; file.Submission.Participant.SubmissionStatus = SubmissionStatus.Uploading;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<SubmissionSummaryDto> GetStatusAsync(Guid submissionId, CancellationToken cancellationToken) => GetAsync(submissionId, cancellationToken);

    public async Task<FinalizeSubmissionResponse> FinalizeAsync(Guid submissionId, FinalizeSubmissionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var submission = await db.SubmissionsSet.Include(x => x.Files).Include(x => x.Participant).ThenInclude(x => x.Session).ThenInclude(x => x.Exam).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.SubmissionNotFound, "Không tìm thấy bài nộp.", 404);
        if (submission.Status is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted)
        {
            var descriptors = submission.Files.Select(ToDescriptor).ToList();
            await transaction.CommitAsync(cancellationToken);
            return new FinalizeSubmissionResponse(submission.Status, submission.ServerReceivedAtUtc!.Value, submission.IsLate, submission.ReceiptCode!, submission.ReceiptSignature!, descriptors);
        }
        EnsureSessionAcceptsSubmission(submission.Participant.Session.Status);
        if (SubmissionStatePolicy.IsCompletedSubmissionStatus(submission.Status))
            throw new ApiException(
                ErrorCodes.SubmissionAlreadyCompleted,
                "Bài nộp đã đóng.",
                409);
        submission.Status = SubmissionStatus.Verifying;
        var finalRoot = paths.SubmissionRoot(submission.SessionId, submission.Participant.StudentCode, submission.Id); Directory.CreateDirectory(finalRoot);
        var completedFiles = new List<FileDescriptorDto>();
        try
        {
            foreach (var file in submission.Files)
            {
                var finalPath = Path.Combine(finalRoot, file.StoredName);
                await chunks.AssembleAndVerifyAsync(file.TemporaryPath, file.TotalChunks, file.SizeBytes, file.Sha256, finalPath, cancellationToken);
                if (!await ArchiveSignatureValidator.MatchesExtensionAsync(finalPath, file.OriginalName, cancellationToken))
                {
                    File.Delete(finalPath);
                    await audit.WriteAsync("SubmissionArchiveRejected", nameof(SubmissionFile), file.Id.ToString(), submission.SessionId, null, new { file.OriginalName, reason = ErrorCodes.SubmissionArchiveRequired }, cancellationToken);
                    throw new ApiException(ErrorCodes.SubmissionArchiveRequired, "Bài làm phải là file nén hợp lệ và chữ ký file phải khớp phần mở rộng.", 422);
                }
                file.RelativePath = Path.GetRelativePath(paths.RootPath, finalPath); file.TransferStatus = TransferStatus.Completed;
                completedFiles.Add(ToDescriptor(file));
            }
        }
        catch
        {
            submission.Status = SubmissionStatus.Failed;
            submission.Participant.SubmissionStatus = SubmissionStatus.Failed;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw;
        }
        var receivedAt = DateTimeOffset.UtcNow; submission.ServerReceivedAtUtc = receivedAt; submission.IsLate = receivedAt > submission.DeadlineUtc; submission.Status = submission.IsLate ? SubmissionStatus.LateSubmitted : SubmissionStatus.Submitted;
        submission.Participant.SubmissionStatus = submission.Status; submission.ClientNote = request.ClientNote;
        var previousOfficial = await db.SubmissionsSet.Where(x => x.ParticipantId == submission.ParticipantId && x.IsOfficial).ToListAsync(cancellationToken);
        foreach (var old in previousOfficial) old.IsOfficial = false;
        submission.IsOfficial = true;
        var signed = receipts.Create(submission.Id, receivedAt, completedFiles); submission.ReceiptCode = signed.ReceiptCode; submission.ReceiptSignature = signed.Signature; submission.Participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        var receiptRoot = paths.ReceiptRoot(submission.SessionId); Directory.CreateDirectory(receiptRoot);
        var receiptPath = Path.Combine(receiptRoot, submission.Id.ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new { submissionId = submission.Id, signed.ReceiptCode, signed.Signature, serverReceivedAtUtc = receivedAt, submission.IsLate, files = completedFiles }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            throw;
        }
        await audit.WriteAsync("SubmissionAccepted", nameof(Submission), submission.Id.ToString(), submission.SessionId, null, new { submission.Status, submission.IsLate, submission.ReceiptCode }, cancellationToken);
        await outbox.EnqueueAsync("submissions", submission.Id.ToString(), "upsert", ToCloud(submission), cancellationToken: cancellationToken);
        foreach (var file in submission.Files)
        {
            var fullPath = Path.Combine(paths.RootPath, file.RelativePath);
            await outbox.EnqueueAsync(
                "submission_files",
                file.Id.ToString(),
                "upsert",
                ToCloud(file),
                fullPath,
                cancellationToken);
        }

        await realtime.PublishSessionAsync(submission.SessionId, RealtimeEvents.SubmissionAccepted, submission.Participant.Session.Sequence, new SubmissionAcceptedEvent(submission.Id, submission.ParticipantId, submission.ReceiptCode, submission.IsLate), cancellationToken);
        await realtime.PublishParticipantAsync(submission.SessionId, submission.ParticipantId, RealtimeEvents.ReceiptCreated, submission.Participant.Session.Sequence, new { submissionId = submission.Id, receiptCode = submission.ReceiptCode }, cancellationToken);
        return new FinalizeSubmissionResponse(submission.Status, receivedAt, submission.IsLate, submission.ReceiptCode, submission.ReceiptSignature, completedFiles);
    }

    public async Task<ReceiptDto> GetReceiptAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var submission = await db.SubmissionsSet.AsNoTracking().Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        if (submission.ServerReceivedAtUtc is null || submission.ReceiptCode is null || submission.ReceiptSignature is null) throw new ApiException(ErrorCodes.Conflict, "Bài nộp chưa có biên nhận.", 409);
        return new ReceiptDto(submission.Id, submission.ReceiptCode, submission.ReceiptSignature, submission.ServerReceivedAtUtc.Value, submission.IsLate, submission.Files.Select(ToDescriptor).ToList());
    }

    public async Task<PagedResult<SubmissionSummaryDto>> ListForSessionAsync(Guid sessionId, SubmissionStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.SubmissionsSet.AsNoTracking().Include(x => x.Files).Include(x => x.Participant).Where(x => x.SessionId == sessionId);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var total = await query.CountAsync(cancellationToken); var rows = await query.OrderBy(x => x.Participant.StudentCode).ThenByDescending(x => x.AttemptNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(ToSummary).ToList(), page, pageSize, total);
    }

    public async Task<SubmissionSummaryDto> GetAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var entity = await db.SubmissionsSet.AsNoTracking().Include(x => x.Files).Include(x => x.Participant).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        return ToSummary(entity);
    }

    public async Task RejectAsync(Guid submissionId, RejectSubmissionRequest request, CancellationToken cancellationToken)
    {
        await _submissionMutations.RejectAsync(
            submissionId,
            request,
            cancellationToken);
    }

    public async Task AllowResubmitAsync(Guid participantId, AllowResubmitRequest request, CancellationToken cancellationToken)
    {
        await _submissionMutations.AllowResubmitAsync(
            participantId,
            request,
            cancellationToken);
    }

    private InitSubmissionResponse ToInitResponse(Submission s) => new(s.Id, s.AttemptNumber, _options.Transfer.ChunkSizeBytes, s.Files.Select(f => new ChunkPlanDto(f.Id, f.TotalChunks, Enumerable.Range(0, f.TotalChunks).Except(chunks.ReadReceivedChunks(f.ReceivedChunksJson)).ToList())).ToList(), s.DeadlineUtc);
    private SubmissionSummaryDto ToSummary(Submission s) => new(s.Id, s.SessionId, s.ParticipantId, s.Participant.StudentCode, s.Participant.DisplayName, s.AttemptNumber, s.Status, s.ClientSubmittedAtUtc, s.ServerReceivedAtUtc, s.DeadlineUtc, s.IsLate, s.ReceiptCode, s.IsOfficial, s.Files.Select(f => f.ToDto(chunks.ReadReceivedChunks(f.ReceivedChunksJson))).ToList());
    private static FileDescriptorDto ToDescriptor(SubmissionFile f) => new(f.Id, f.OriginalName, f.SizeBytes, f.Sha256, f.MimeType, $"/api/v1/submissions/{f.SubmissionId}/files/{f.Id}/content");
    private static object ToCloud(Submission x) =>
        SubmissionMutationPayloads.ToCloud(x);

    private static object ToCloud(SubmissionFile x) => new
    {
        id = x.Id,
        submission_id = x.SubmissionId,
        client_file_id = x.ClientFileId,
        name = x.OriginalName,
        stored_name = x.StoredName,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        mime_type = x.MimeType,
        transfer_status = x.TransferStatus.ToString(),
        sync_status = x.SyncStatus.ToString(),
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private async Task<InitSubmissionResponse> ResolveConcurrentInitAsync(
        InitSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await db.SubmissionsSet
            .AsNoTracking()
            .Include(x => x.Files)
            .FirstOrDefaultAsync(
                x => x.ParticipantId == request.ParticipantId
                    && x.IdempotencyKey == request.IdempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            EnsureIdempotencyMatches(existing, request);
            return ToInitResponse(existing);
        }

        var statuses = await db.SubmissionsSet
            .AsNoTracking()
            .Where(x => x.ParticipantId == request.ParticipantId)
            .Select(x => x.Status)
            .ToListAsync(cancellationToken);
        if (statuses.Any(SubmissionStatePolicy.IsActiveSubmissionStatus))
        {
            throw new ApiException(
                ErrorCodes.SubmissionAlreadyProcessing,
                "Đã có một lần nộp bài đang được xử lý.",
                409);
        }

        throw new ApiException(
            ErrorCodes.SubmissionIdempotencyConflict,
            "Không thể xác nhận kết quả khởi tạo đồng thời.",
            409);
    }

    private static void EnsureSessionAcceptsSubmission(SessionStatus status)
    {
        if (!SubmissionStatePolicy.SessionAcceptsSubmission(status))
        {
            throw new ApiException(
                ErrorCodes.SessionSubmissionNotOpen,
                "Phòng thi hiện không nhận bài nộp.",
                409);
        }
    }

    private static void EnsureIdempotencyMatches(
        Submission existing,
        InitSubmissionRequest request)
    {
        var filesMatch = existing.Files.Count == request.Files.Count
            && request.Files.All(input => existing.Files.Any(file =>
                file.ClientFileId == input.ClientFileId
                && file.OriginalName == Path.GetFileName(input.Name)
                && file.SizeBytes == input.SizeBytes
                && file.Sha256.Equals(input.Sha256, StringComparison.OrdinalIgnoreCase)
                && file.MimeType == input.MimeType));
        if (existing.SessionId != request.SessionId || !filesMatch)
        {
            throw new ApiException(
                ErrorCodes.SubmissionIdempotencyConflict,
                "IdempotencyKey đã được dùng cho yêu cầu khởi tạo khác.",
                409);
        }
    }

    private static bool IsSubmissionUniquenessViolation(DbUpdateException exception)
    {
        if (exception.InnerException is not SqliteException sqlite
            || sqlite.SqliteErrorCode != 19
            || sqlite.SqliteExtendedErrorCode != 2067)
        {
            return false;
        }

        return sqlite.Message.Contains("submissions", StringComparison.OrdinalIgnoreCase)
            && sqlite.Message.Contains("ParticipantId", StringComparison.OrdinalIgnoreCase)
            && (sqlite.Message.Contains("AttemptNumber", StringComparison.OrdinalIgnoreCase)
                || sqlite.Message.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateFiles(IReadOnlyList<InitSubmissionFileRequest> files)
    {
        if (files.Count != StudentSubmissionPolicy.MaxFileCount)
            throw new ApiException(ErrorCodes.SubmissionFileCountInvalid, "Bài nộp phải có đúng một file nén.");
        foreach (var f in files)
        {
            if (!StudentSubmissionPolicy.IsAllowedExtension(f.Name))
                throw new ApiException(ErrorCodes.SubmissionArchiveRequired, "Bài làm phải được nén thành một file .zip, .rar hoặc .7z trước khi nộp.");
            if (f.SizeBytes <= 0 || f.SizeBytes > StudentSubmissionPolicy.MaxBytes)
                throw new ApiException(ErrorCodes.SubmissionTooLarge, "File bài làm vượt quá 10 MB. Hãy xóa dữ liệu không cần thiết hoặc giảm dung lượng rồi nén lại.");
            if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) throw new ApiException(ErrorCodes.ValidationFailed, $"SHA-256 của {f.Name} không hợp lệ.");
        }
    }

}
