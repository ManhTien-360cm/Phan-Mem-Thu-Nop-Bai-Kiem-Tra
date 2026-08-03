using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class GradeService(
    AppDbContext db,
    IStoragePaths paths,
    IChunkStorage chunks,
    IAuditService audit,
    IOutboxService outbox,
    ICloudAdapter? cloud = null) : IGradeService
{
    private const decimal AuthoritativeMaxScore = 10.00m;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<SubmissionSummaryDto>> GetQueueAsync(
        GradingStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.SubmissionsSet.AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.Participant)
            .Include(x => x.Session)
            .Where(x => x.Session.DeliveryTypeSnapshot == ExamDeliveryType.FileSubmission
                && x.IsOfficial
                && (x.Status == SubmissionStatus.Submitted || x.Status == SubmissionStatus.LateSubmitted));
        if (status.HasValue)
        {
            query = status.Value switch
            {
                GradingStatus.NotGraded => query.Where(x => !db.GradesSet.Any(g => g.SubmissionId == x.Id)),
                _ => query.Where(x => db.GradesSet.Any(g => g.SubmissionId == x.Id && g.Status == status.Value))
            };
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.Participant.StudentCode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = rows.Select(s => new SubmissionSummaryDto(
            s.Id,
            s.SessionId,
            s.ParticipantId,
            s.Participant.StudentCode,
            s.Participant.DisplayName,
            s.AttemptNumber,
            s.Status,
            s.ClientSubmittedAtUtc,
            s.ServerReceivedAtUtc,
            s.DeadlineUtc,
            s.IsLate,
            s.ReceiptCode,
            s.IsOfficial,
            s.Files.Select(f => f.ToDto([])).ToList())).ToList();
        return new(items, page, pageSize, total);
    }

    public async Task<GradeDto> GetAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        _ = await db.SubmissionsSet.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        var grade = await GradeQuery(asTracking: false)
            .FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken);
        return grade is null ? Empty(submissionId) : ToDto(grade);
    }

    public async Task<GradeDto> GetTeacherAsync(
        Guid submissionId,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        var (_, submission) = await RequireGradeableSubmissionAsync(
            submissionId,
            actorId,
            actorOrganizationId,
            cancellationToken);
        if (submission.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var result = await RequireCloud().GetPublicEssayGradeAsync(submissionId, cancellationToken);
            EnsureCloudResult(result, submission);
            await CacheCloudResultAsync(result, cancellationToken);
            return ToDto(result);
        }

        var grade = await GradeQuery(asTracking: false)
            .FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken);
        return grade is null ? Empty(submissionId) : ToDto(grade);
    }

    public async Task<GradeDto> SaveAsync(
        Guid submissionId,
        SaveGradeRequest request,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        ValidateSave(request);
        var (_, submission) = await RequireGradeableSubmissionAsync(
            submissionId,
            actorId,
            actorOrganizationId,
            cancellationToken);
        if (submission.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloudResult = await RequireCloud().SavePublicEssayGradeAsync(
                submissionId,
                request.Score,
                request.RubricScores,
                request.GeneralComment,
                await ResolveCloudVersionAsync(submissionId, request.RowVersion, cancellationToken),
                ResolveRequestId(request.MutationRequestId),
                cancellationToken);
            EnsureCloudResult(cloudResult, submission);
            await CacheCloudResultAsync(cloudResult, cancellationToken);
            return ToDto(cloudResult);
        }

        var requestHash = HashRequest(new
        {
            submissionId,
            request.Score,
            maxScore = AuthoritativeMaxScore,
            request.RubricScores,
            generalComment = Normalize(request.GeneralComment),
            request.RowVersion
        });
        var cached = await FindReceiptAsync(
            request.MutationRequestId,
            submissionId,
            actorId,
            "SaveEssayGrade",
            requestHash,
            cancellationToken);
        if (cached is not null)
            return cached;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var grade = await GradeQuery(asTracking: true)
            .FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken);
        if (grade is null)
        {
            if (!string.Equals(request.RowVersion, "new", StringComparison.Ordinal))
                throw Conflict();
            grade = new Grade
            {
                SubmissionId = submissionId,
                Status = GradingStatus.Graded,
                MaxScore = AuthoritativeMaxScore,
                SourceMode = "Lan"
            };
            db.GradesSet.Add(grade);
        }
        else
        {
            EnsureLocalConcurrency(grade, request.RowVersion);
            if (grade.Status == GradingStatus.Returned)
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Kết quả đã trả; cần mở lại trước khi sửa.",
                    409);
        }

        var before = grade.Revision == 0 ? null : ToDto(grade);
        grade.Score = request.Score;
        grade.MaxScore = AuthoritativeMaxScore;
        grade.GeneralComment = Normalize(request.GeneralComment);
        grade.Status = GradingStatus.Graded;
        grade.GraderId = actorId;
        grade.GradedAtUtc = DateTimeOffset.UtcNow;
        grade.ReturnedAtUtc = null;
        grade.Revision++;
        db.RubricScoresSet.RemoveRange(grade.RubricScores);
        grade.RubricScores = request.RubricScores.Select(x => new RubricScore
        {
            GradeId = grade.Id,
            CriterionKey = x.CriterionKey.Trim(),
            Title = x.Title.Trim(),
            Score = x.Score,
            MaxScore = x.MaxScore,
            Comment = Normalize(x.Comment),
            Order = x.Order
        }).ToList();
        await db.SaveChangesAsync(cancellationToken);

        var result = ToDto(grade);
        await audit.WriteAsync(
            "GradeSaved",
            nameof(Grade),
            grade.Id.ToString(),
            submission.SessionId,
            before,
            new { ActorId = actorId, Grade = result },
            cancellationToken);
        await EnqueueOnlyLanProjectionAsync(grade, cancellationToken);
        await StoreReceiptAsync(
            request.MutationRequestId,
            submissionId,
            actorId,
            "SaveEssayGrade",
            requestHash,
            result,
            null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<GradeDto> ReturnAsync(
        Guid submissionId,
        ReturnGradeRequest request,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        var (_, submission) = await RequireGradeableSubmissionAsync(
            submissionId,
            actorId,
            actorOrganizationId,
            cancellationToken);
        if (submission.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloudResult = await RequireCloud().ReturnPublicEssayGradeAsync(
                submissionId,
                request.Message,
                await ResolveCloudVersionAsync(submissionId, request.RowVersion, cancellationToken),
                ResolveRequestId(request.MutationRequestId),
                cancellationToken);
            EnsureCloudResult(cloudResult, submission);
            await CacheCloudResultAsync(cloudResult, cancellationToken);
            return ToDto(cloudResult);
        }

        var requestHash = HashRequest(new
        {
            submissionId,
            message = Normalize(request.Message),
            request.RowVersion
        });
        var cached = await FindReceiptAsync(
            request.MutationRequestId,
            submissionId,
            actorId,
            "ReturnEssayGrade",
            requestHash,
            cancellationToken);
        if (cached is not null)
            return cached;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var grade = await GradeQuery(asTracking: true)
            .Include(x => x.Submission).ThenInclude(x => x.Session)
            .FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Chưa có kết quả chấm.", 404);
        if (grade.Status == GradingStatus.Returned && request.MutationRequestId == Guid.Empty)
            return ToDto(grade);
        EnsureLocalConcurrencyIfPresent(grade, request.RowVersion);
        if (grade.Status != GradingStatus.Graded)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ có thể trả bài đã chấm.", 409);
        if (grade.Score is null || grade.Score < 0 || grade.Score > AuthoritativeMaxScore)
            throw new ApiException(ErrorCodes.ValidationFailed, "Điểm không hợp lệ để trả kết quả.");

        var returnedAt = DateTimeOffset.UtcNow;
        grade.Status = GradingStatus.Returned;
        grade.ReturnedAtUtc = returnedAt;
        grade.GraderId = actorId;
        grade.GradedAtUtc ??= returnedAt;
        grade.Revision++;
        grade.Submission.Session.Sequence++;
        var eventId = Guid.NewGuid();
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.GradeReturned,
            grade.Submission.SessionId,
            grade.Submission.Session.Sequence,
            participantId: grade.Submission.ParticipantId,
            submissionId: submissionId,
            message: request.Message,
            score: grade.Score,
            maxScore: AuthoritativeMaxScore,
            occurredAtUtc: returnedAt,
            eventId: eventId);
        await db.SaveChangesAsync(cancellationToken);

        var result = ToDto(grade);
        await audit.WriteAsync(
            "GradeReturned",
            nameof(Grade),
            grade.Id.ToString(),
            grade.Submission.SessionId,
            null,
            new { ActorId = actorId, Grade = result, Message = Normalize(request.Message) },
            cancellationToken);
        await EnqueueOnlyLanProjectionAsync(grade, cancellationToken);
        await StoreReceiptAsync(
            request.MutationRequestId,
            submissionId,
            actorId,
            "ReturnEssayGrade",
            requestHash,
            result,
            eventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<GradeDto> ReopenAsync(
        Guid submissionId,
        ReopenGradeRequest request,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(ErrorCodes.ValidationFailed, "Phải có lý do mở lại điểm.");
        var (_, submission) = await RequireGradeableSubmissionAsync(
            submissionId,
            actorId,
            actorOrganizationId,
            cancellationToken);
        if (submission.Session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloudResult = await RequireCloud().ReopenPublicEssayGradeAsync(
                submissionId,
                request.Reason,
                await ResolveCloudVersionAsync(submissionId, request.RowVersion, cancellationToken),
                ResolveRequestId(request.MutationRequestId),
                cancellationToken);
            EnsureCloudResult(cloudResult, submission);
            await CacheCloudResultAsync(cloudResult, cancellationToken);
            return ToDto(cloudResult);
        }

        var requestHash = HashRequest(new
        {
            submissionId,
            reason = request.Reason.Trim(),
            request.RowVersion
        });
        var cached = await FindReceiptAsync(
            request.MutationRequestId,
            submissionId,
            actorId,
            "ReopenEssayGrade",
            requestHash,
            cancellationToken);
        if (cached is not null)
            return cached;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var grade = await GradeQuery(asTracking: true)
            .Include(x => x.Submission).ThenInclude(x => x.Session)
            .FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy kết quả chấm.", 404);
        EnsureLocalConcurrencyIfPresent(grade, request.RowVersion);
        if (grade.Status != GradingStatus.Returned)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ mở lại kết quả đã trả.", 409);

        var before = ToDto(grade);
        grade.Status = GradingStatus.Graded;
        grade.ReturnedAtUtc = null;
        grade.GraderId = actorId;
        grade.Revision++;
        grade.Submission.Session.Sequence++;
        var eventId = Guid.NewGuid();
        OnlyLanStudentNotificationOutbox.Enqueue(
            db,
            StudentNotificationEventType.GradeReopened,
            grade.Submission.SessionId,
            grade.Submission.Session.Sequence,
            participantId: grade.Submission.ParticipantId,
            submissionId: submissionId,
            reason: request.Reason,
            eventId: eventId);
        await db.SaveChangesAsync(cancellationToken);

        var result = ToDto(grade);
        await audit.WriteAsync(
            "GradeReopened",
            nameof(Grade),
            grade.Id.ToString(),
            grade.Submission.SessionId,
            before,
            new { ActorId = actorId, Grade = result, Reason = request.Reason.Trim() },
            cancellationToken);
        await EnqueueOnlyLanProjectionAsync(grade, cancellationToken);
        await StoreReceiptAsync(
            request.MutationRequestId,
            submissionId,
            actorId,
            "ReopenEssayGrade",
            requestHash,
            result,
            eventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<FileDescriptorDto> AddAttachmentAsync(
        Guid submissionId,
        string fileName,
        string mimeType,
        Stream content,
        long contentLength,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        if (contentLength <= 0 || contentLength > 100L * 1024 * 1024)
            throw new ApiException(ErrorCodes.FileTooLarge, "File đính kèm không hợp lệ hoặc quá lớn.");
        var (_, submission) = await RequireGradeableSubmissionAsync(
            submissionId,
            actorId,
            actorOrganizationId,
            cancellationToken);
        var grade = await db.GradesSet.Include(x => x.Attachments)
            .FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Chưa có bản ghi chấm bài.", 404);
        if (grade.Status == GradingStatus.Returned)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Cần mở lại kết quả trước khi thêm phản hồi.", 409);

        var id = Guid.NewGuid();
        var stored = id.ToString("N") + Path.GetExtension(fileName).ToLowerInvariant();
        var root = Path.Combine(paths.SessionRoot(submission.SessionId), "graded", submissionId.ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, stored);
        await using (var output = new FileStream(
            path + ".tmp",
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            true))
        {
            await content.CopyToAsync(output, cancellationToken);
        }
        File.Move(path + ".tmp", path);
        var hash = await chunks.ComputeSha256Async(path, cancellationToken);
        var entity = new GradedAttachment
        {
            Id = id,
            GradeId = grade.Id,
            OriginalName = Path.GetFileName(fileName),
            StoredName = stored,
            RelativePath = Path.GetRelativePath(paths.RootPath, path),
            SizeBytes = new FileInfo(path).Length,
            Sha256 = hash,
            MimeType = mimeType
        };
        db.GradedAttachmentsSet.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "GradedAttachmentAdded",
            nameof(GradedAttachment),
            entity.Id.ToString(),
            submission.SessionId,
            null,
            new { ActorId = actorId, entity.OriginalName, entity.SizeBytes, entity.Sha256 },
            cancellationToken);
        await outbox.EnqueueAsync(
            "graded_attachments",
            entity.Id.ToString(),
            "upsert",
            ToCloud(entity),
            path,
            cancellationToken);
        return new(
            entity.Id,
            entity.OriginalName,
            entity.SizeBytes,
            entity.Sha256,
            entity.MimeType,
            $"/api/v1/grading/submissions/{submissionId}/attachments/{entity.Id}/content");
    }

    public async Task<byte[]> ExportGradebookCsvAsync(Guid? sessionId, CancellationToken cancellationToken)
    {
        var query = db.GradesSet.AsNoTracking()
            .Include(x => x.Submission).ThenInclude(x => x.Participant)
            .AsQueryable();
        if (sessionId.HasValue)
            query = query.Where(x => x.Submission.SessionId == sessionId.Value);
        var grades = await query.OrderBy(x => x.Submission.Participant.StudentCode)
            .ToListAsync(cancellationToken);
        var sb = new StringBuilder("studentCode,displayName,score,maxScore,status,gradedAtUtc,returnedAtUtc\n");
        foreach (var grade in grades)
        {
            sb.AppendLine($"{E(grade.Submission.Participant.StudentCode)},{E(grade.Submission.Participant.DisplayName)},{grade.Score},{grade.MaxScore},{grade.Status},{grade.GradedAtUtc:O},{grade.ReturnedAtUtc:O}");
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private async Task<(User Actor, Submission Submission)> RequireGradeableSubmissionAsync(
        Guid submissionId,
        Guid actorId,
        string? actorOrganizationId,
        CancellationToken cancellationToken)
    {
        var actor = await db.UsersSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actorId, cancellationToken);
        if (actor is null || !actor.IsActive || actor.Role is not (UserRole.Teacher or UserRole.Admin))
            throw new ApiException(ErrorCodes.Forbidden, "Không được phép chấm bài.", 403);

        var submission = await db.SubmissionsSet
            .Include(x => x.Files)
            .Include(x => x.Participant)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .SingleOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp hợp lệ.", 404);
        if (submission.ParticipantId != submission.Participant.Id
            || submission.Participant.SessionId != submission.SessionId
            || submission.Session.ExamId != submission.Session.Exam.Id
            || submission.Session.DeliveryTypeSnapshot != ExamDeliveryType.FileSubmission
            || submission.Session.Exam.DeliveryType != ExamDeliveryType.FileSubmission
            || !submission.IsOfficial
            || !SubmissionStatePolicy.IsCompletedSubmissionStatus(submission.Status)
            || submission.Files.Count != StudentSubmissionPolicy.MaxFileCount)
        {
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài nộp không đủ điều kiện chấm EssayFile.", 409);
        }
        if (submission.Session.AccessMode == SessionAccessMode.LanOnly
            && (submission.Files.Single().TransferStatus != TransferStatus.Completed
                || string.Equals(submission.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài nộp OnlyLAN chưa hoàn tất.", 409);
        }
        if (submission.Session.AccessMode == SessionAccessMode.PublicCloud
            && !string.Equals(submission.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài nộp không thuộc PublicCloud.", 409);
        }

        await EnsureOwnershipAsync(
            actor,
            actorOrganizationId,
            submission.Session.Exam.CreatedBy,
            cancellationToken);
        return (actor, submission);
    }

    private async Task EnsureOwnershipAsync(
        User actor,
        string? actorOrganizationId,
        Guid? ownerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorOrganizationId)
            || string.IsNullOrWhiteSpace(actor.OrganizationId)
            || !string.Equals(actor.OrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "Không xác định được tổ chức của người chấm.", 403);
        }
        if (!ownerId.HasValue)
            throw new ApiException(ErrorCodes.Forbidden, "Không xác định được quyền sở hữu bài thi.", 403);
        if (ownerId.Value == actor.Id)
            return;
        var ownerOrganizationId = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == ownerId.Value && x.IsActive)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(ownerOrganizationId)
            || !string.Equals(ownerOrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "Không được chấm bài thuộc tổ chức khác.", 403);
        }
    }

    private IQueryable<Grade> GradeQuery(bool asTracking)
    {
        var query = db.GradesSet
            .Include(x => x.RubricScores)
            .Include(x => x.Attachments)
            .AsQueryable();
        return asTracking ? query : query.AsNoTracking();
    }

    private static void ValidateSave(SaveGradeRequest request)
    {
        if (request.Score is < 0 or > AuthoritativeMaxScore)
            throw new ApiException(ErrorCodes.ValidationFailed, "Điểm phải nằm trong khoảng 0 đến 10.");
        if (request.RubricScores is null)
            throw new ApiException(ErrorCodes.ValidationFailed, "Danh sách rubric không hợp lệ.");
        if (request.RubricScores.Any(x => string.IsNullOrWhiteSpace(x.CriterionKey)
                || string.IsNullOrWhiteSpace(x.Title)
                || x.MaxScore <= 0
                || x.Score < 0
                || x.Score > x.MaxScore))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Điểm rubric không hợp lệ.");
        }
        if (request.RubricScores.GroupBy(x => x.CriterionKey.Trim(), StringComparer.Ordinal)
            .Any(x => x.Count() > 1))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Rubric không được trùng tiêu chí.");
        }
        if (request.RubricScores.Count > 0
            && request.RubricScores.Sum(x => x.MaxScore) > AuthoritativeMaxScore)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Tổng điểm tối đa rubric vượt thang điểm của bài thi.");
        }
    }

    private async Task<GradeDto?> FindReceiptAsync(
        Guid requestId,
        Guid submissionId,
        Guid actorId,
        string action,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
            return null;
        var receipt = await db.EssayGradeMutationReceiptsSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);
        if (receipt is null)
            return null;
        if (receipt.SubmissionId != submissionId
            || receipt.ActorId != actorId
            || !string.Equals(receipt.Action, action, StringComparison.Ordinal)
            || !string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "MutationRequestId đã được dùng cho nội dung khác.");
        }
        return JsonSerializer.Deserialize<GradeDto>(receipt.ResultJson, Json)
            ?? throw new ApiException(ErrorCodes.InvalidStateTransition, "Biên nhận mutation không hợp lệ.", 500);
    }

    private async Task StoreReceiptAsync(
        Guid requestId,
        Guid submissionId,
        Guid actorId,
        string action,
        string requestHash,
        GradeDto result,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
            return;
        db.EssayGradeMutationReceiptsSet.Add(new EssayGradeMutationReceipt
        {
            Id = requestId,
            SubmissionId = submissionId,
            ActorId = actorId,
            Action = action,
            RequestHash = requestHash,
            ResultJson = JsonSerializer.Serialize(result, Json),
            EventId = eventId,
            GradeRevision = result.Revision
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnqueueOnlyLanProjectionAsync(Grade grade, CancellationToken cancellationToken)
    {
        await outbox.EnqueueAsync(
            "grades",
            grade.Id.ToString(),
            "upsert",
            ToCloud(grade),
            cancellationToken: cancellationToken);
        foreach (var rubric in grade.RubricScores)
        {
            await outbox.EnqueueAsync(
                "rubric_scores",
                rubric.Id.ToString(),
                "upsert",
                ToCloud(rubric),
                cancellationToken: cancellationToken);
        }
    }

    private async Task<long> ResolveCloudVersionAsync(
        Guid submissionId,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        if (long.TryParse(rowVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && version >= 0)
        {
            return version;
        }
        var current = await RequireCloud().GetPublicEssayGradeAsync(submissionId, cancellationToken);
        return current.CloudVersion;
    }

    private async Task CacheCloudResultAsync(
        CloudEssayGradeResult result,
        CancellationToken cancellationToken)
    {
        if (!result.GradeId.HasValue)
            return;
        var grade = await GradeQuery(asTracking: true)
            .FirstOrDefaultAsync(x => x.SubmissionId == result.SubmissionId, cancellationToken);
        if (grade is null)
        {
            grade = new Grade { Id = result.GradeId.Value, SubmissionId = result.SubmissionId };
            db.GradesSet.Add(grade);
        }
        else if (grade.Id != result.GradeId.Value)
        {
            throw new ApiException(ErrorCodes.CloudUploadFailed, "Định danh grade PublicCloud không khớp cache cục bộ.", 502);
        }
        grade.SourceMode = "PublicCloud";
        grade.CloudVersion = result.CloudVersion;
        grade.CloudUpdatedAtUtc = result.UpdatedAtUtc;
        grade.CloudSyncState = "Pulled";
        grade.Status = result.Status;
        grade.Score = result.Score;
        grade.MaxScore = result.MaxScore;
        grade.GeneralComment = result.GeneralComment;
        grade.GraderId = result.GraderId;
        grade.GradedAtUtc = result.GradedAtUtc;
        grade.ReturnedAtUtc = result.ReturnedAtUtc;
        grade.Revision = result.Revision;
        db.RubricScoresSet.RemoveRange(grade.RubricScores);
        grade.RubricScores = result.RubricScores.Select(x => new RubricScore
        {
            GradeId = grade.Id,
            CriterionKey = x.CriterionKey,
            Title = x.Title,
            Score = x.Score,
            MaxScore = x.MaxScore,
            Comment = x.Comment,
            Order = x.Order
        }).ToList();
        db.GradedAttachmentsSet.RemoveRange(grade.Attachments);
        grade.Attachments = result.Attachments.Select(x => new GradedAttachment
        {
            Id = x.Id,
            GradeId = grade.Id,
            OriginalName = x.Name,
            StoredName = string.Empty,
            RelativePath = string.Empty,
            SizeBytes = x.SizeBytes,
            Sha256 = x.Sha256,
            MimeType = x.MimeType,
            SyncStatus = SyncStatus.Synced
        }).ToList();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureCloudResult(CloudEssayGradeResult result, Submission submission)
    {
        if (result.SubmissionId != submission.Id
            || result.SessionId != submission.SessionId
            || result.ParticipantId != submission.ParticipantId
            || result.MaxScore != AuthoritativeMaxScore
            || result.CloudVersion < 0
            || result.Revision < 0)
        {
            throw new ApiException(ErrorCodes.CloudUploadFailed, "Supabase trả contract grade không khớp bài đang mở.", 502);
        }
    }

    private static void EnsureLocalConcurrency(Grade grade, string rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)
            || !string.Equals(grade.RowVersion, rowVersion, StringComparison.Ordinal))
        {
            throw Conflict();
        }
    }

    private static void EnsureLocalConcurrencyIfPresent(Grade grade, string rowVersion)
    {
        if (!string.IsNullOrWhiteSpace(rowVersion))
            EnsureLocalConcurrency(grade, rowVersion);
    }

    private static ApiException Conflict() => new(
        ErrorCodes.ConcurrencyConflict,
        "Điểm đã được cập nhật ở nơi khác.",
        409);

    private ICloudAdapter RequireCloud() => cloud ?? throw new ApiException(
        ErrorCodes.CloudOffline,
        "PublicCloud chưa được cấu hình cho thao tác chấm EssayFile.",
        503);

    private static Guid ResolveRequestId(Guid requestId) =>
        requestId == Guid.Empty ? Guid.NewGuid() : requestId;

    private static string HashRequest(object value) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static GradeDto Empty(Guid submissionId) => new(
        submissionId,
        GradingStatus.NotGraded,
        null,
        AuthoritativeMaxScore,
        [],
        null,
        [],
        null,
        "new")
    {
        GradeId = null,
        Revision = 0
    };

    private static GradeDto ToDto(Grade grade) => new(
        grade.SubmissionId,
        grade.Status,
        grade.Score,
        AuthoritativeMaxScore,
        grade.RubricScores.OrderBy(x => x.Order).Select(x => new RubricScoreDto(
            x.CriterionKey,
            x.Title,
            x.Score,
            x.MaxScore,
            x.Comment,
            x.Order)).ToList(),
        grade.GeneralComment,
        grade.Attachments.Select(x => new FileDescriptorDto(
            x.Id,
            x.OriginalName,
            x.SizeBytes,
            x.Sha256,
            x.MimeType,
            string.Equals(grade.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"/api/v1/grading/submissions/{grade.SubmissionId}/attachments/{x.Id}/content")).ToList(),
        grade.ReturnedAtUtc,
        string.Equals(grade.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)
            ? grade.CloudVersion.ToString(CultureInfo.InvariantCulture)
            : grade.RowVersion)
    {
        GradeId = grade.Id,
        Revision = grade.Revision
    };

    private static GradeDto ToDto(CloudEssayGradeResult grade) => new(
        grade.SubmissionId,
        grade.Status,
        grade.Score,
        grade.MaxScore,
        grade.RubricScores,
        grade.GeneralComment,
        grade.Attachments.Select(x => new FileDescriptorDto(
            x.Id,
            x.Name,
            x.SizeBytes,
            x.Sha256,
            x.MimeType,
            null)).ToList(),
        grade.ReturnedAtUtc,
        grade.CloudVersion.ToString(CultureInfo.InvariantCulture))
    {
        GradeId = grade.GradeId,
        Revision = grade.Revision
    };

    private static object ToCloud(Grade grade) => new
    {
        id = grade.Id,
        submission_id = grade.SubmissionId,
        status = grade.Status.ToString(),
        score = grade.Score,
        max_score = AuthoritativeMaxScore,
        general_comment = grade.GeneralComment,
        grader_id = grade.GraderId,
        graded_at = grade.GradedAtUtc,
        returned_at = grade.ReturnedAtUtc,
        revision = grade.Revision,
        created_at = grade.CreatedAtUtc,
        updated_at = grade.UpdatedAtUtc
    };

    private static object ToCloud(RubricScore rubric) => new
    {
        id = rubric.Id,
        grade_id = rubric.GradeId,
        criterion_key = rubric.CriterionKey,
        title = rubric.Title,
        score = rubric.Score,
        max_score = rubric.MaxScore,
        comment = rubric.Comment,
        sort_order = rubric.Order,
        created_at = rubric.CreatedAtUtc,
        updated_at = rubric.UpdatedAtUtc
    };

    private static object ToCloud(GradedAttachment attachment) => new
    {
        id = attachment.Id,
        grade_id = attachment.GradeId,
        name = attachment.OriginalName,
        size_bytes = attachment.SizeBytes,
        sha256 = attachment.Sha256,
        mime_type = attachment.MimeType,
        created_at = attachment.CreatedAtUtc,
        updated_at = attachment.UpdatedAtUtc
    };

    private static string E(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;
}
