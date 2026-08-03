using System.Security.Cryptography;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SessionService(AppDbContext db, IAuditService audit, IOutboxService outbox, IRealtimePublisher realtime, IOptions<ExamTransferOptions> options, ILogger<SessionService> logger, SessionParticipantMutationDispatcher participantMutations, LanParticipantSessionExecution lanParticipantSessions, PublicCloudProjectionExecution cloudProjection, ICloudSyncSignal? cloudSyncSignal = null, ICloudAdapter? cloudAdapter = null) : ISessionService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly ExamTransferOptions _options = options.Value;
    private readonly SessionParticipantMutationDispatcher _participantMutations = participantMutations;
    private readonly LanParticipantSessionExecution _lanParticipantSessions = lanParticipantSessions;
    private readonly PublicCloudProjectionExecution _cloudProjection = cloudProjection;

    public async Task<PagedResult<SessionSummaryDto>> ListAsync(SessionStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var baseQuery = db.ExamSessionsSet.AsNoTracking().AsQueryable();
        if (status.HasValue)
            baseQuery = baseQuery.Where(x => x.Status == status.Value);
        else
            baseQuery = baseQuery.Where(x => x.Status != SessionStatus.Archived);

        var total = await baseQuery.CountAsync(cancellationToken);
        var sortKeys = await baseQuery
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
        var rows = await db.ExamSessionsSet
            .AsNoTracking()
            .Where(x => pageIds.Contains(x.Id))
            .Include(x => x.Exam)
            .Include(x => x.Participants)
            .ToListAsync(cancellationToken);
        var items = rows
            .OrderBy(x => position[x.Id])
            .Select(ToSummary)
            .ToList();

        return new(items, page, pageSize, total);
    }

    public async Task<SessionDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet.AsNoTracking().Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        return ToDetail(session);
    }

    public async Task<SessionDetailDto> CreateAsync(CreateSessionRequest request, string hostDeviceId, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(async () =>
        {
            var session = await CreateCoreAsync(request, hostDeviceId, cancellationToken);
            await audit.WriteAsync("SessionCreated", nameof(ExamSession), session.Id.ToString(), session.Id, null, ToCloud(session), cancellationToken);
            await outbox.EnqueueAsync("exam_sessions", session.Id.ToString(), "upsert", ToCloud(session), cancellationToken: cancellationToken);
            return ToDetail(session);
        }, cancellationToken);
    }

    public async Task<SessionDetailDto> CreateAndOpenAsync(CreateSessionRequest request, string hostDeviceId, CancellationToken cancellationToken)
    {
        var detail = await InTransactionAsync(async () =>
        {
            var session = await CreateCoreAsync(request, hostDeviceId, cancellationToken);
            await audit.WriteAsync("SessionCreated", nameof(ExamSession), session.Id.ToString(), session.Id, null, ToCloud(session), cancellationToken);
            var before = session.Status;
            session.TransitionTo(SessionStatus.Waiting);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync(
                "SessionStateChanged",
                nameof(ExamSession),
                session.Id.ToString(),
                session.Id,
                new { status = before },
                new { status = session.Status, reason = "CreateAndOpen" },
                cancellationToken);
            await outbox.EnqueueAsync(
                "exam_sessions",
                session.Id.ToString(),
                "upsert",
                ToCloud(session),
                cancellationToken: cancellationToken);
            return ToDetail(session);
        }, cancellationToken);
        cloudSyncSignal?.Pulse();
        await PublishSessionStateSafeAsync(detail, cancellationToken);
        return detail;
    }

    public Task<CloudProjectionReadiness> GetProjectionReadinessAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        _cloudProjection.GetProjectionReadinessAsync(id, cancellationToken);

    public Task<CloudProjectionReadiness> RetryProjectionAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        _cloudProjection.RetryProjectionAsync(id, cancellationToken);

    public async Task<SessionDetailDto> ChangePublicCloudRoomCodeAsync(
        Guid id,
        ChangePublicCloudRoomCodeRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await InTransactionAsync(async () =>
        {
            var session = await db.ExamSessionsSet
                .Include(x => x.Exam)
                .Include(x => x.Participants)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
            if (session.AccessMode != SessionAccessMode.PublicCloud)
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Chỉ phòng PublicCloud mới có thể đổi mã bằng luồng phục hồi projection.",
                    409);
            if (session.Status is not (SessionStatus.Draft or SessionStatus.Waiting))
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Chỉ có thể đổi mã phòng trước khi kỳ thi bắt đầu.",
                    409);
            EnsureRowVersion(session.RowVersion, request.RowVersion);

            var projectionItems = await db.SyncQueueSet
                .Where(x => x.EntityType == "exam_sessions" && x.EntityId == id.ToString())
                .ToListAsync(cancellationToken);
            var projection = projectionItems
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefault()
                ?? throw new ApiException(
                    ErrorCodes.Conflict,
                    "Không tìm thấy projection PublicCloud để phục hồi.",
                    409);
            if (!PublicCloudProjectionExecution.IsRoomCodeConflict(projection))
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Chỉ đổi mã bằng luồng này khi projection trả ROOM_CODE_CONFLICT.",
                    409);

            var hasBusinessActivity = session.Participants.Count > 0
                || await db.SubmissionsSet.AnyAsync(x => x.SessionId == id, cancellationToken)
                || await db.QuizAttemptsSet.AnyAsync(x => x.SessionId == id, cancellationToken)
                || await db.MessagesSet.AnyAsync(x => x.SessionId == id, cancellationToken);
            if (hasBusinessActivity)
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Không thể đổi mã vì phòng đã có hoạt động của học sinh hoặc giáo viên.",
                    409);

            var nextRoomCode = string.IsNullOrWhiteSpace(request.NewRoomCode)
                ? await GenerateRoomCodeAsync(cancellationToken)
                : RoomCodeRules.Normalize(request.NewRoomCode);
            if (!RoomCodeRules.IsValid(nextRoomCode))
                throw new ApiException(
                    ErrorCodes.ValidationFailed,
                    RoomCodeRules.ValidationMessage,
                    422);
            if (string.Equals(session.RoomCode, nextRoomCode, StringComparison.Ordinal))
                throw new ApiException(
                    ErrorCodes.RoomCodeConflict,
                    "Mã mới phải khác mã PublicCloud đang bị trùng.",
                    409);
            if (await db.ExamSessionsSet.AnyAsync(
                    x => x.Id != id
                        && x.RoomCode == nextRoomCode
                        && x.Status != SessionStatus.Archived
                        && x.Status != SessionStatus.Cancelled
                        && x.Status != SessionStatus.Finished,
                    cancellationToken))
                throw new ApiException(
                    ErrorCodes.RoomCodeConflict,
                    "Mã phòng đang được sử dụng cục bộ.",
                    409);

            var previousRoomCode = session.RoomCode;
            session.RoomCode = nextRoomCode;
            await db.SaveChangesAsync(cancellationToken);

            projection.PayloadJson = JsonSerializer.Serialize(ToCloud(session), JsonOptions);
            projection.Status = SyncStatus.Pending;
            projection.RetryCount = 0;
            projection.NextRetryAtUtc = DateTimeOffset.UtcNow;
            projection.LastError = null;
            projection.LeaseUntilUtc = null;
            projection.LastAttemptAtUtc = null;
            projection.CompletedAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync(
                "PublicCloudRoomCodeChanged",
                nameof(ExamSession),
                session.Id.ToString(),
                session.Id,
                new { roomCode = previousRoomCode },
                new { roomCode = nextRoomCode },
                cancellationToken);
            return ToDetail(session);
        }, cancellationToken);
        cloudSyncSignal?.Pulse();
        return detail;
    }

    public async Task<SessionDetailDto> UpdateAsync(Guid id, UpdateSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet.Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.Status is not (SessionStatus.Draft or SessionStatus.Waiting)) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ sửa phòng trước khi bắt đầu thi.", 409);
        EnsureRowVersion(session.RowVersion, request.RowVersion);
        ValidateSessionConfiguration(request.SettingsJson, request.Capacity);
        session.PlannedStartUtc = request.PlannedStartUtc;
        session.SettingsJson = string.IsNullOrWhiteSpace(request.SettingsJson) ? "{}" : request.SettingsJson;
        var pending = session.Participants.Where(x => x.Status == ParticipantStatus.PendingApproval).ToList();
        if (!session.AutoApprove && request.AutoApprove && pending.Count > 0 && !request.ApprovePendingParticipants)
            throw new ApiException(ErrorCodes.Conflict, "Cần xác nhận trước khi duyệt toàn bộ yêu cầu đang chờ.", 409, details: new { pendingCount = pending.Count, requiresConfirmation = true });
        session.AutoApprove = request.AutoApprove;
        if (request.AutoApprove && request.ApprovePendingParticipants)
        {
            foreach (var participant in pending)
            {
                participant.Status = ParticipantStatus.Approved;
                participant.ApprovedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        session.Capacity = request.Capacity;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("SessionUpdated", nameof(ExamSession), session.Id.ToString(), session.Id, null, ToCloud(session), cancellationToken);
        await outbox.EnqueueAsync(
            "exam_sessions",
            session.Id.ToString(),
            "upsert",
            ToCloud(session),
            cancellationToken: cancellationToken);
        return ToDetail(session);
    }

    public async Task<SessionDetailDto> TransitionAsync(Guid id, SessionStatus target, EndSessionRequest? endRequest, CancellationToken cancellationToken)
    {
        var detail = await InTransactionAsync(async () =>
        {
            var session = await db.ExamSessionsSet.Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
            if (target is SessionStatus.Finished or SessionStatus.Cancelled)
            {
                var activeUploads = await db.SubmissionsSet.AnyAsync(x => x.SessionId == id && (x.Status == SubmissionStatus.Uploading || x.Status == SubmissionStatus.Verifying), cancellationToken);
                if (activeUploads && endRequest?.Force != true) throw new ApiException(ErrorCodes.Conflict, "Đang có bài nộp upload; cần force=true và lý do để kết thúc.", 409);
                if (endRequest?.Force == true && string.IsNullOrWhiteSpace(endRequest.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Kết thúc cưỡng bức phải có lý do.");
            }
            var before = session.Status;
            session.TransitionTo(target);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("SessionStateChanged", nameof(ExamSession), session.Id.ToString(), session.Id, new { status = before }, new { status = session.Status, reason = endRequest?.Reason }, cancellationToken);
            await outbox.EnqueueAsync("exam_sessions", session.Id.ToString(), "upsert", ToCloud(session), cancellationToken: cancellationToken);
            return ToDetail(session);
        }, cancellationToken);
        await PublishSessionStateSafeAsync(detail, cancellationToken);
        return detail;
    }

    public async Task<BulkArchiveResultDto> BulkArchiveAsync(
        BulkArchiveRequest request,
        CancellationToken cancellationToken)
    {
        var ids = BulkArchiveValidation.Validate(request);
        var archivedDetails = new List<SessionDetailDto>();
        var result = await InTransactionAsync(async () =>
        {
            var sessions = await db.ExamSessionsSet
                .Include(x => x.Exam)
                .Include(x => x.Participants)
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
            var found = sessions.Select(x => x.Id).ToHashSet();
            var missing = ids.Where(id => !found.Contains(id)).ToList();
            if (missing.Count > 0)
                throw new ApiException(
                    ErrorCodes.NotFound,
                    "Một hoặc nhiều kỳ thi không tồn tại.",
                    404,
                    details: new { missingIds = missing });

            var alreadyArchived = sessions
                .Where(x => x.Status == SessionStatus.Archived)
                .Select(x => x.Id)
                .Order()
                .ToList();
            var toArchive = sessions
                .Where(x => x.Status != SessionStatus.Archived)
                .OrderBy(x => x.Id)
                .ToList();
            var rejected = toArchive
                .Where(x => x.Status is not (SessionStatus.Finished or SessionStatus.Cancelled))
                .Select(x => new BulkArchiveFailureDto(
                    x.Id,
                    ErrorCodes.InvalidStateTransition,
                    $"Kỳ thi {x.RoomCode} đang ở trạng thái {x.Status}."))
                .ToList();
            if (rejected.Count > 0)
                throw new ApiException(
                    ErrorCodes.InvalidStateTransition,
                    "Chỉ kỳ thi đã kết thúc hoặc đã hủy mới được lưu trữ.",
                    409,
                    details: new { rejected });

            foreach (var session in toArchive)
            {
                var before = session.Status;
                session.TransitionTo(SessionStatus.Archived);
                await db.SaveChangesAsync(cancellationToken);
                await audit.WriteAsync(
                    "SessionStateChanged",
                    nameof(ExamSession),
                    session.Id.ToString(),
                    session.Id,
                    new { status = before },
                    new { status = session.Status, reason = "BulkArchive" },
                    cancellationToken);
                await outbox.EnqueueAsync(
                    "exam_sessions",
                    session.Id.ToString(),
                    "upsert",
                    ToCloud(session),
                    cancellationToken: cancellationToken);
                archivedDetails.Add(ToDetail(session));
            }

            return new BulkArchiveResultDto(
                ids.Count,
                toArchive.Count,
                alreadyArchived,
                []);
        }, cancellationToken);

        foreach (var detail in archivedDetails)
            await PublishSessionStateSafeAsync(detail, cancellationToken);
        return result;
    }

    public Task<JoinSessionResponse> JoinAsync(JoinSessionRequest request, Guid accountUserId, string studentCode, string displayName, string? ipAddress, CancellationToken cancellationToken) =>
        _lanParticipantSessions.JoinAsync(
            request,
            accountUserId,
            studentCode,
            displayName,
            ipAddress,
            cancellationToken);

    public async Task<ParticipantDto> ApproveAsync(Guid sessionId, Guid participantId, Guid mutationRequestId, CancellationToken cancellationToken)
    {
        return await _participantMutations.ApproveAsync(
            sessionId,
            participantId,
            mutationRequestId,
            cancellationToken);
    }

    public async Task RejectAsync(Guid sessionId, Guid participantId, string? reason, Guid mutationRequestId, CancellationToken cancellationToken)
    {
        await _participantMutations.RejectAsync(
            sessionId,
            participantId,
            reason,
            mutationRequestId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDto>> BulkApproveAsync(Guid sessionId, BulkApproveRequest request, CancellationToken cancellationToken)
    {
        return await _participantMutations.BulkApproveAsync(
            sessionId,
            request,
            cancellationToken);
    }

    public async Task<ParticipantDto> AddExtraTimeAsync(Guid sessionId, Guid participantId, ExtraTimeRequest request, CancellationToken cancellationToken)
    {
        return await _participantMutations.AddExtraTimeAsync(
            sessionId,
            participantId,
            request,
            cancellationToken);
    }

    public async Task<MessageDto> SendMessageAsync(Guid sessionId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 2000) throw new ApiException(ErrorCodes.ValidationFailed, "Nội dung thông báo không hợp lệ.");
        var session = await db.ExamSessionsSet.FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (request.ReceiverParticipantId.HasValue)
        {
            var validReceiver = await db.SessionParticipantsSet.AnyAsync(
                x => x.Id == request.ReceiverParticipantId.Value
                    && x.SessionId == sessionId
                    && x.Status != ParticipantStatus.Rejected,
                cancellationToken);
            if (!validReceiver)
                throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người nhận hợp lệ trong phòng thi.", 404);
        }
        if (session.AccessMode == SessionAccessMode.PublicCloud)
        {
            var cloud = cloudAdapter
                ?? throw new ApiException(
                    ErrorCodes.CloudUploadFailed,
                    "PublicCloud chưa được cấu hình cho thông báo giáo viên.",
                    503);
            return await cloud.SendPublicTeacherMessageAsync(
                sessionId,
                request.ReceiverParticipantId,
                request.Type,
                request.Content.Trim(),
                Guid.NewGuid(),
                cancellationToken);
        }
        var message = new Message { SessionId = sessionId, ReceiverId = request.ReceiverParticipantId, Type = request.Type, Content = request.Content.Trim() };
        db.MessagesSet.Add(message);
        session.Sequence++;
        if (session.AccessMode == SessionAccessMode.LanOnly)
        {
            OnlyLanStudentNotificationOutbox.Enqueue(
                db,
                StudentNotificationEventType.TeacherMessageReceived,
                sessionId,
                session.Sequence,
                participantId: request.ReceiverParticipantId,
                message: message.Content);
        }
        await db.SaveChangesAsync(cancellationToken);
        var dto = new MessageDto(message.Id, message.SessionId, message.SenderId, message.ReceiverId, message.Type, message.Content, message.CreatedAtUtc);
        return dto;
    }

    public Task<HeartbeatResponse> HeartbeatAsync(Guid sessionId, Guid participantId, string deviceId, HeartbeatRequest request, CancellationToken cancellationToken) =>
        _lanParticipantSessions.HeartbeatAsync(
            sessionId,
            participantId,
            deviceId,
            request,
            cancellationToken);

    public async Task<ParticipantDto> GetParticipantAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken)
    {
        var entity = await db.SessionParticipantsSet.AsNoTracking()
            .Include(x => x.Session)
            .ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        return entity.ToDto(
            DateTimeOffset.UtcNow,
            _options.Session.DisconnectAfterSeconds,
            ParticipantDeadline(entity));
    }

    private SessionDetailDto ToDetail(ExamSession session) => new(
        ToSummary(session),
        session.Participants
            .OrderBy(x => x.StudentCode)
            .Select(x => x.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds, ParticipantDeadline(x)))
            .ToList(),
        session.SettingsJson,
        session.PlannedStartUtc,
        session.Capacity);
    private SessionSummaryDto ToSummary(ExamSession s)
    {
        var p = s.Participants; var now = DateTimeOffset.UtcNow;
        var counts = new SessionCountsDto(p.Count, p.Count(x => x.Status == ParticipantStatus.PendingApproval), p.Count(x => x.Status == ParticipantStatus.Approved), p.Count(x => x.LastSeenUtc.HasValue && now - x.LastSeenUtc <= TimeSpan.FromSeconds(_options.Session.DisconnectAfterSeconds)), p.Count(x => x.SubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted), p.Count(x => x.SubmissionStatus == SubmissionStatus.Uploading), p.Count(x => x.Status == ParticipantStatus.Disconnected));
        return new SessionSummaryDto(s.Id, s.ExamId, s.Exam.Title, s.RoomCode, s.Status, now, s.StartedAtUtc, s.EndedAtUtc, EffectiveDeadline(s), counts, s.Sequence, s.RowVersion, s.AccessMode, s.AutoApprove, s.DeliveryTypeSnapshot, s.SupervisionModeSnapshot, s.QuizResultPolicySnapshot, s.ExamVersionSnapshot, s.AdmissionMode);
    }
    private static DateTimeOffset? EffectiveDeadline(ExamSession s) => s.StartedAtUtc?.AddMinutes(s.Exam.DurationMinutes);
    private static DateTimeOffset? ParticipantDeadline(SessionParticipant participant) =>
        SessionParticipantMutationRules.ParticipantDeadline(participant);
    private async Task<string> GenerateRoomCodeAsync(CancellationToken cancellationToken)
    {
        const string chars = RoomCodeRules.GeneratedAlphabet;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var bytes = RandomNumberGenerator.GetBytes(_options.Security.RoomCodeLength); var code = new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
            if (!await db.ExamSessionsSet.AnyAsync(x => x.RoomCode == code && x.Status != SessionStatus.Archived && x.Status != SessionStatus.Cancelled && x.Status != SessionStatus.Finished, cancellationToken)) return code;
        }
        throw new ApiException(ErrorCodes.RoomCodeConflict, "Không thể sinh mã phòng không trùng.", 500);
    }
    private static object ToCloud(ExamSession x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        class_id = x.ClassId,
        room_code = x.RoomCode,
        status = x.Status.ToString(),
        host_device_id = x.HostDeviceId,
        planned_start_at = x.PlannedStartUtc,
        started_at = x.StartedAtUtc,
        ended_at = x.EndedAtUtc,
        delivery_type = x.DeliveryTypeSnapshot.ToString(),
        supervision_mode = x.SupervisionModeSnapshot.ToString(),
        quiz_result_policy = x.QuizResultPolicySnapshot.ToString(),
        exam_version = x.ExamVersionSnapshot,
        settings_json = x.SettingsJson,
        auto_approve = x.AutoApprove,
        access_mode = x.AccessMode.ToString(),
        admission_mode = x.AdmissionMode.ToString(),
        capacity = x.Capacity,
        accepting_participants = x.AcceptingParticipants,
        sequence = x.Sequence,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private async Task PublishSessionStateSafeAsync(SessionDetailDto detail, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishSessionAsync(
                detail.Summary.Id,
                RealtimeEvents.SessionStateChanged,
                detail.Summary.Sequence,
                new SessionStateChangedEvent(detail.Summary.Status, DateTimeOffset.UtcNow, detail.Summary.EffectiveDeadlineUtc),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Realtime publish failed after local session transition committed. SessionId={SessionId}; Status={Status}; Sequence={Sequence}",
                detail.Summary.Id,
                detail.Summary.Status,
                detail.Summary.Sequence);
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

    private async Task<ExamSession> CreateCoreAsync(
        CreateSessionRequest request,
        string hostDeviceId,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.AdmissionMode))
            throw new ApiException(ErrorCodes.ValidationFailed, "Chế độ tiếp nhận không hợp lệ.", 422);
        var exam = await db.ExamsSet.FirstOrDefaultAsync(x => x.Id == request.ExamId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
        if (exam.Status != ExamStatus.Published)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ tạo phòng từ bài kiểm tra đã phát hành.", 409);
        if (exam.DeliveryType == ExamDeliveryType.MultipleChoice
            && exam.SupervisionMode != SupervisionMode.Standard)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Trắc nghiệm bắt buộc dùng giám sát chuẩn.", 409);
        ValidateSessionConfiguration(request.SettingsJson, request.Capacity);

        Guid? effectiveClassId;
        ClassRoom? classroom = null;
        if (request.AdmissionMode == SessionAdmissionMode.OpenRequest)
        {
            if (request.ClassId.HasValue)
                throw new ApiException(
                    ErrorCodes.ValidationFailed,
                    "OpenRequest không được gắn ClassId.",
                    422);
            effectiveClassId = null;
        }
        else
        {
            effectiveClassId = request.ClassId ?? exam.ClassId;
            if (!effectiveClassId.HasValue)
                throw new ApiException(
                    ErrorCodes.ValidationFailed,
                    "ClassMembersOnly bắt buộc phải có lớp học.",
                    422);
            if (request.ClassId.HasValue && exam.ClassId.HasValue && request.ClassId.Value != exam.ClassId.Value)
                throw new ApiException(ErrorCodes.ValidationFailed, "Lớp của phòng thi phải trùng với lớp của bài kiểm tra.", 422);
            classroom = await db.ClassesSet.FirstOrDefaultAsync(x => x.Id == effectiveClassId.Value, cancellationToken)
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp được chọn.", 404);
            if (request.AccessMode == SessionAccessMode.PublicCloud
                && classroom.AccessMode != ClassAccessMode.Public)
                throw new ApiException(ErrorCodes.ValidationFailed, "Chỉ lớp public mới có thể tạo phòng PublicCloud theo lớp.", 422);
        }

        var roomCode = string.IsNullOrWhiteSpace(request.CustomRoomCode)
            ? await GenerateRoomCodeAsync(cancellationToken)
            : RoomCodeRules.Normalize(request.CustomRoomCode);
        if (!RoomCodeRules.IsValid(roomCode))
            throw new ApiException(ErrorCodes.ValidationFailed, RoomCodeRules.ValidationMessage);
        if (await db.ExamSessionsSet.AnyAsync(
                x => x.RoomCode == roomCode
                    && x.Status != SessionStatus.Archived
                    && x.Status != SessionStatus.Cancelled
                    && x.Status != SessionStatus.Finished,
                cancellationToken))
            throw new ApiException(ErrorCodes.RoomCodeConflict, "Mã phòng đang được sử dụng.", 409);

        var session = new ExamSession
        {
            ExamId = request.ExamId,
            Exam = exam,
            ClassId = effectiveClassId,
            RoomCode = roomCode,
            HostDeviceId = hostDeviceId,
            PlannedStartUtc = request.PlannedStartUtc,
            SettingsJson = string.IsNullOrWhiteSpace(request.SettingsJson) ? "{}" : request.SettingsJson,
            AutoApprove = request.AutoApprove,
            Capacity = request.Capacity,
            Status = SessionStatus.Draft,
            AcceptingParticipants = true,
            AccessMode = request.AccessMode,
            AdmissionMode = request.AdmissionMode,
            DeliveryTypeSnapshot = exam.DeliveryType,
            SupervisionModeSnapshot = exam.SupervisionMode,
            QuizResultPolicySnapshot = exam.QuizResultPolicy,
            ExamVersionSnapshot = exam.Version
        };
        db.ExamSessionsSet.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    private static void ValidateSessionConfiguration(string? settingsJson, int? capacity)
    {
        if (capacity.HasValue && (capacity.Value <= 0 || capacity.Value > 5000))
            throw new ApiException(ErrorCodes.ValidationFailed, "Sức chứa phòng phải nằm trong khoảng 1-5000.");

        if (string.IsNullOrWhiteSpace(settingsJson))
            return;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Cấu hình phòng thi phải là JSON object hợp lệ.");
        }
    }

    private static void EnsureRowVersion(string current, string supplied) { if (current != supplied) throw new ApiException(ErrorCodes.ConcurrencyConflict, "Dữ liệu đã thay đổi.", 409, details: new { currentRowVersion = current }); }
}
