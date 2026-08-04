using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class StudentRealtimeService : IStudentRealtimeService
{
    private readonly IBackendClient api;
    private readonly StudentSessionState session;
    private readonly SupabaseRealtimeService publicRealtime;
    private readonly SupabasePublicCloudClient publicCloud;
    private readonly SemaphoreSlim gate = new(1, 1);
    private RealtimeService? realtime;
    private Guid? activeSessionId;
    private Guid? activeParticipantId;
    private ExamTransfer.Shared.Contracts.SessionAccessMode? activeMode;

    public bool IsConnected => session.AccessMode == ExamTransfer.Shared.Contracts.SessionAccessMode.PublicCloud
        ? publicRealtime.IsConnected
        : realtime?.IsConnected == true;
    public bool IsRunning => activeSessionId.HasValue
        && (activeMode == ExamTransfer.Shared.Contracts.SessionAccessMode.PublicCloud
            ? publicRealtime.IsRunning
            : realtime is not null);
    public Guid? ActiveSessionId => activeSessionId;
    public event EventHandler<string>? EventReceived;
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

    public StudentRealtimeService(
        IBackendClient api,
        StudentSessionState session,
        SupabaseRealtimeService publicRealtime,
        SupabasePublicCloudClient publicCloud)
    {
        this.api = api;
        this.session = session;
        this.publicRealtime = publicRealtime;
        this.publicCloud = publicCloud;
        publicRealtime.EventReceived += Forward;
        publicRealtime.NotificationReceived += ForwardNotification;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var nextSessionId = session.SessionId;
            var nextParticipantId = session.ParticipantId;
            var nextMode = session.AccessMode;
            if (nextSessionId.HasValue
                && nextParticipantId.HasValue
                && activeSessionId == nextSessionId
                && activeParticipantId == nextParticipantId
                && activeMode == nextMode
                && IsRunning)
                return;

            await StopCoreAsync(ct);
            if (!nextSessionId.HasValue || !nextParticipantId.HasValue)
                return;
            activeSessionId = nextSessionId;
            activeParticipantId = nextParticipantId;
            activeMode = nextMode;
            if (nextMode == ExamTransfer.Shared.Contracts.SessionAccessMode.PublicCloud)
            {
                await publicRealtime.StartAsync(
                    nextSessionId.Value,
                    nextParticipantId.Value,
                    Environment.MachineName + "-" + Environment.UserName,
                    publicCloud,
                    async token => _ = await publicCloud.GetStudentTimelineAsync(
                        nextSessionId.Value,
                        token),
                    (revision, eventId, limit, token) =>
                        publicCloud.GetStudentNotificationEventsAsync(
                            nextSessionId.Value,
                            revision,
                            eventId,
                            limit,
                            token),
                    ct);
                return;
            }
            if (string.IsNullOrWhiteSpace(session.AccessToken))
            {
                ClearIdentity();
                return;
            }
            realtime = new RealtimeService(
                api.BaseAddress.ToString(),
                RealtimeAuthenticationMode.ParticipantHeader);
            realtime.EventReceived += Forward;
            realtime.NotificationReceived += ForwardNotification;
            await realtime.ConnectAsync(session.AccessToken, ct);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync(ct);
        }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        publicRealtime.Stop();
        if (realtime is not null)
        {
            realtime.EventReceived -= Forward;
            realtime.NotificationReceived -= ForwardNotification;
            await realtime.DisconnectAsync(ct);
            await realtime.DisposeAsync();
            realtime = null;
        }
        ClearIdentity();
    }

    private void ClearIdentity()
    {
        activeSessionId = null;
        activeParticipantId = null;
        activeMode = null;
    }

    private void Forward(object? sender, string eventName) => EventReceived?.Invoke(this, eventName);
    private void ForwardNotification(object? sender, StudentRealtimeNotification value) =>
        NotificationReceived?.Invoke(this, value);

    public void Dispose()
    {
        publicRealtime.EventReceived -= Forward;
        publicRealtime.NotificationReceived -= ForwardNotification;
        StopAsync().GetAwaiter().GetResult();
        gate.Dispose();
    }
}
