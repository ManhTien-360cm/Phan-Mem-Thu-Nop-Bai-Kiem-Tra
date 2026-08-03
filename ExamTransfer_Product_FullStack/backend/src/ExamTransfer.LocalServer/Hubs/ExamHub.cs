using System.Security.Claims;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Hubs;

[Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account + "," + ExamTransferAuthSchemes.ExamParticipant)]
public sealed class ExamHub(
    ISessionService sessions,
    IControlService control,
    AppDbContext db,
    OnlyLanStudentNotificationDispatcher notificationDispatcher) : Hub
{
    public static string SessionGroup(Guid id) => $"session:{id:N}";
    public static string StudentSessionGroup(Guid id) => $"student-session:{id:N}";
    public static string ParticipantGroup(Guid sessionId, Guid participantId) => $"session:{sessionId:N}:participant:{participantId:N}";

    public override async Task OnConnectedAsync()
    {
        if (TryIds(out var sessionId, out var participantId))
        {
            var participantIdentity = Context.User?.Identities.FirstOrDefault(identity =>
                identity.IsAuthenticated
                && string.Equals(
                    identity.AuthenticationType,
                    ExamTransferAuthSchemes.ExamParticipant,
                    StringComparison.Ordinal));
            var claimedDeviceId = participantIdentity?.FindFirst("device_id")?.Value;
            var claimedUserId = Guid.TryParse(
                participantIdentity?.FindFirst("user_id")?.Value,
                out var parsedUserId)
                ? parsedUserId
                : Guid.Empty;
            var participant = await db.SessionParticipantsSet
                .AsNoTracking()
                .Include(x => x.Session)
                .SingleOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, Context.ConnectionAborted);
            if (participantIdentity is null
                || !participantIdentity.HasClaim(ClaimTypes.Role, nameof(UserRole.Student))
                || participant is null
                || participant.Session.AccessMode != SessionAccessMode.LanOnly
                || string.IsNullOrWhiteSpace(claimedDeviceId)
                || !string.Equals(participant.DeviceId, claimedDeviceId, StringComparison.Ordinal)
                || (participant.UserId.HasValue && participant.UserId.Value != claimedUserId))
                throw new HubException(ErrorCodes.Unauthorized);

            await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
            await Groups.AddToGroupAsync(Context.ConnectionId, StudentSessionGroup(sessionId));
            await Groups.AddToGroupAsync(Context.ConnectionId, ParticipantGroup(sessionId, participantId));
            Context.Items[typeof(ExamHub)] = (sessionId, participantId);
            await notificationDispatcher.ReplayAsync(
                sessionId,
                participantId,
                Context.ConnectionId,
                Context.ConnectionAborted);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(typeof(ExamHub), out var value)
            && value is ValueTuple<Guid, Guid> scope)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(scope.Item1));
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, StudentSessionGroup(scope.Item1));
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ParticipantGroup(scope.Item1, scope.Item2));
            Context.Items.Remove(typeof(ExamHub));
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeSession(Guid sessionId)
    {
        EnsureTeacherOrAdmin();
        await EnsureSessionExistsAsync(sessionId);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SessionGroup(sessionId),
            Context.ConnectionAborted);
    }

    public async Task UnsubscribeSession(Guid sessionId)
    {
        EnsureTeacherOrAdmin();
        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            SessionGroup(sessionId),
            Context.ConnectionAborted);
    }

    public async Task Heartbeat(HeartbeatRequest request)
    {
        EnsureIds(out var sid, out var pid, out var deviceId);
        await sessions.HeartbeatAsync(sid, pid, deviceId, request, Context.ConnectionAborted);
    }

    public async Task ClientReady(ClientReadyRequest request)
    {
        EnsureIds(out var sid, out var pid, out _);
        var participant = await db.SessionParticipantsSet.FirstOrDefaultAsync(x => x.Id == pid && x.SessionId == sid, Context.ConnectionAborted) ?? throw new HubException(ErrorCodes.NotFound);
        participant.CapabilityJson = System.Text.Json.JsonSerializer.Serialize(request.Capabilities); await db.SaveChangesAsync(Context.ConnectionAborted);
    }

    public async Task DownloadProgress(long bytes, long totalBytes, DownloadStatus status)
    {
        EnsureIds(out var sid, out var pid, out _);
        var p = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == pid && x.SessionId == sid, Context.ConnectionAborted) ?? throw new HubException(ErrorCodes.NotFound);
        p.DownloadStatus = status; p.Session.Sequence++; await db.SaveChangesAsync(Context.ConnectionAborted);
        var percent = totalBytes <= 0 ? 0 : Math.Clamp(bytes * 100d / totalBytes, 0, 100);
        await Clients.Group(SessionGroup(sid)).SendAsync(RealtimeEvents.DownloadProgressChanged, new RealtimeEnvelope<DownloadProgressEvent>(Guid.NewGuid(), sid, p.Session.Sequence, DateTimeOffset.UtcNow, RealtimeEvents.DownloadProgressChanged, new DownloadProgressEvent(pid, percent, bytes, status)), Context.ConnectionAborted);
    }

    public Task ViolationReport(ViolationReportRequest request)
    {
        EnsureIds(out var sid, out var pid, out _); return control.ReportViolationAsync(sid, pid, request, Context.ConnectionAborted);
    }

    public Task PolicyApplyAck(PolicyApplyAckRequest request)
    {
        EnsureIds(out var sid, out var pid, out _); return control.PolicyAckAsync(sid, pid, request, Context.ConnectionAborted);
    }

    private bool TryIds(out Guid sessionId, out Guid participantId)
    {
        var sessionValid = Guid.TryParse(
            Context.User?.FindFirstValue("session_id"),
            out sessionId);

        var participantValid = Guid.TryParse(
            Context.User?.FindFirstValue("participant_id"),
            out participantId);

        return sessionValid && participantValid;
    }

    private void EnsureTeacherOrAdmin()
    {
        var accountIdentity = Context.User?.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                ExamTransferAuthSchemes.Account,
                StringComparison.Ordinal));
        var role = accountIdentity?.FindFirst(ClaimTypes.Role)?.Value;
        if (role is not (nameof(UserRole.Teacher) or nameof(UserRole.Admin)))
            throw new HubException(ErrorCodes.Unauthorized);
    }

    private async Task EnsureSessionExistsAsync(Guid sessionId)
    {
        if (sessionId == Guid.Empty
            || !await db.ExamSessionsSet
                .AsNoTracking()
                .AnyAsync(x => x.Id == sessionId
                    && x.AccessMode == SessionAccessMode.LanOnly,
                    Context.ConnectionAborted))
            throw new HubException(ErrorCodes.NotFound);
    }

    private void EnsureIds(out Guid sessionId, out Guid participantId, out string deviceId)
    {
        if (!TryIds(out sessionId, out participantId)) throw new HubException(ErrorCodes.Unauthorized);
        deviceId = Context.User?.FindFirstValue("device_id") ?? throw new HubException(ErrorCodes.Unauthorized);
    }
}

