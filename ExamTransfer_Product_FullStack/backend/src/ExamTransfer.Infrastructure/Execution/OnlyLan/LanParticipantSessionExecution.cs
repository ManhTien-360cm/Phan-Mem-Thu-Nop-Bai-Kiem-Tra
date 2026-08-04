using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Execution.OnlyLan;

public sealed class LanParticipantSessionExecution(
    AppDbContext db,
    ISessionTokenService tokens,
    IAuditService audit,
    IOutboxService outbox,
    IRealtimePublisher realtime,
    IOptions<ExamTransferOptions> options,
    ILanAccessPolicy lanAccessPolicy,
    ILogger<LanParticipantSessionExecution>? logger = null,
    IHttpContextAccessor? httpContextAccessor = null)
{
    private readonly ExamTransferOptions _options = options.Value;

    public async Task<JoinSessionResponse> JoinAsync(
        JoinSessionRequest request,
        Guid accountUserId,
        string studentCode,
        string displayName,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RoomCode) || string.IsNullOrWhiteSpace(studentCode) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(request.DeviceId))
            throw new ApiException(ErrorCodes.ValidationFailed, "Mã phòng, danh tính tài khoản và Device ID là bắt buộc.");
        if (!string.IsNullOrWhiteSpace(request.StudentCode) && !request.StudentCode.Trim().Equals(studentCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(ErrorCodes.ParticipantAccountMismatch, "Mã sinh viên trong yêu cầu không khớp với tài khoản đăng nhập.", 403);
        if (!string.IsNullOrWhiteSpace(request.DisplayName) && !request.DisplayName.Trim().Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(ErrorCodes.ParticipantAccountMismatch, "Họ tên trong yêu cầu không khớp với tài khoản đăng nhập.", 403);
        var roomCode = RoomCodeRules.Normalize(request.RoomCode);
        var session = await db.ExamSessionsSet.Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.RoomCode == roomCode, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.AccessMode == SessionAccessMode.PublicCloud)
            throw new ApiException(
                ErrorCodes.PublicCloudRouteRequired,
                "Phòng PublicCloud phải được tham gia qua RPC PublicCloud.",
                409);
        if (session.Status != SessionStatus.Waiting || !session.AcceptingParticipants) throw new ApiException(ErrorCodes.InvalidStateTransition, "Phòng chưa mở hoặc đã khóa nhận người mới.", 409);
        if (session.AccessMode == SessionAccessMode.LanOnly)
        {
            var decision = lanAccessPolicy.Evaluate(ipAddress);
            var traceId = httpContextAccessor?.HttpContext?.TraceIdentifier
                ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
                ?? "unavailable";
            logger?.Log(
                decision.Allowed ? LogLevel.Information : LogLevel.Warning,
                "LAN join access evaluated. RuntimeMode={RuntimeMode}; RemoteIp={RemoteIp}; EffectiveClientIp={EffectiveClientIp}; AllowedCidrs={AllowedCidrs}; MatchedRange={MatchedRange}; DeniedReason={DeniedReason}; SessionId={SessionId}; TraceId={TraceId}",
                decision.RuntimeMode,
                decision.RemoteIp ?? "unknown",
                decision.EffectiveClientIp ?? "unknown",
                decision.AllowedCidrs.Count == 0 ? "<none>" : string.Join(',', decision.AllowedCidrs),
                decision.MatchedRange ?? "<none>",
                decision.DeniedReason,
                session.Id,
                traceId);
            if (!decision.Allowed)
                throw new ApiException(ErrorCodes.LanAccessDenied, "Thiết bị không nằm trong mạng nội bộ được phép của phòng thi.", 403);
        }
        if (session.AdmissionMode == SessionAdmissionMode.ClassMembersOnly)
        {
            if (!session.ClassId.HasValue)
                throw new ApiException(ErrorCodes.InvalidStateTransition, "Phòng giới hạn theo lớp đang thiếu lớp học.", 409);
            var members = await db.ClassMembersSet.AsNoTracking().Where(x => x.ClassId == session.ClassId.Value).ToListAsync(cancellationToken);
            var normalizedCode = studentCode.Trim();
            var isMember = members.Any(x => x.UserId == accountUserId)
                || members.Any(x => !x.UserId.HasValue && x.StudentCode.Trim().Equals(normalizedCode, StringComparison.OrdinalIgnoreCase));
            if (!isMember)
                throw new ApiException(ErrorCodes.Forbidden, "Bạn chưa có tên trong lớp học của phòng này. Hãy liên hệ giáo viên để được thêm vào lớp.", 403);
        }
        if (session.Capacity.HasValue && session.Participants.Count >= session.Capacity.Value) throw new ApiException(ErrorCodes.Conflict, "Phòng đã đủ số lượng.", 409);
        var existing = session.Participants.FirstOrDefault(x => x.StudentCode.Equals(studentCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.UserId.HasValue && existing.UserId.Value != accountUserId)
                throw new ApiException(ErrorCodes.ParticipantAccountMismatch, "Lượt tham gia không thuộc tài khoản đang đăng nhập.", 403);
            if (!existing.DeviceId.Equals(request.DeviceId, StringComparison.Ordinal)) throw new ApiException(ErrorCodes.DuplicateStudentCode, "Mã học sinh đang được dùng trên thiết bị khác.", 409);
            existing.UserId = accountUserId;
            existing.DisplayName = displayName.Trim();
            existing.LastSeenUtc = DateTimeOffset.UtcNow; existing.IpAddress = ipAddress; existing.MachineName = request.MachineName; existing.AppVersion = request.AppVersion;
            if (existing.Status == ParticipantStatus.Disconnected) existing.Status = ParticipantStatus.Connected;
            await db.SaveChangesAsync(cancellationToken);
            await outbox.EnqueueAsync(
                "session_participants",
                existing.Id.ToString(),
                "upsert",
                ToCloud(existing),
                cancellationToken: cancellationToken);
            return CreateJoinResponse(session, existing);
        }
        var participant = new SessionParticipant
        {
            SessionId = session.Id, UserId = accountUserId, StudentCode = studentCode.Trim(), DisplayName = displayName.Trim(), ClassName = request.ClassName?.Trim(),
            DeviceId = request.DeviceId, MachineName = request.MachineName, IpAddress = ipAddress, AppVersion = request.AppVersion,
            Status = session.AutoApprove ? ParticipantStatus.Approved : ParticipantStatus.PendingApproval,
            ApprovedAtUtc = session.AutoApprove ? DateTimeOffset.UtcNow : null, LastSeenUtc = DateTimeOffset.UtcNow
        };
        db.SessionParticipantsSet.Add(participant); session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("ParticipantJoined", nameof(SessionParticipant), participant.Id.ToString(), session.Id, null, ToCloud(participant), cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            ToCloud(participant),
            cancellationToken: cancellationToken);
        await realtime.PublishSessionAsync(session.Id, RealtimeEvents.ParticipantJoined, session.Sequence, participant.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds), cancellationToken);
        return CreateJoinResponse(session, participant);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(
        Guid sessionId,
        Guid participantId,
        string deviceId,
        HeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        if (!participant.DeviceId.Equals(deviceId, StringComparison.Ordinal)) throw new ApiException(ErrorCodes.Forbidden, "Token không thuộc thiết bị này.", 403);
        var serverNowUtc = DateTimeOffset.UtcNow;
        var wasDisconnected = participant.Status == ParticipantStatus.Disconnected;
        participant.LastSeenUtc = serverNowUtc;
        if (wasDisconnected) participant.Status = participant.ApprovedAtUtc.HasValue ? ParticipantStatus.Approved : ParticipantStatus.Connected;
        if (wasDisconnected) participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        if (wasDisconnected)
        {
            await realtime.PublishSessionAsync(sessionId, RealtimeEvents.ParticipantConnectionChanged, participant.Session.Sequence, new ParticipantConnectionChangedEvent(participantId, ConnectionState.Online, participant.LastSeenUtc.Value), cancellationToken);
        }
        return new HeartbeatResponse(serverNowUtc);
    }

    private JoinSessionResponse CreateJoinResponse(ExamSession session, SessionParticipant participant)
    {
        var issued = tokens.IssueParticipantToken(
            session.Id,
            participant.Id,
            participant.UserId ?? Guid.Empty,
            participant.DeviceId,
            participant.Status,
            ParticipantTokenLifetime(session));
        return new JoinSessionResponse(session.Id, participant.Id, participant.Status, issued.Token, issued.ExpiresAtUtc, participant.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds));
    }

    private TimeSpan ParticipantTokenLifetime(ExamSession session) =>
        SessionParticipantMutationRules.ParticipantTokenLifetime(_options, session);

    private static object ToCloud(SessionParticipant participant) =>
        SessionParticipantMutationRules.ToCloud(participant);
}
