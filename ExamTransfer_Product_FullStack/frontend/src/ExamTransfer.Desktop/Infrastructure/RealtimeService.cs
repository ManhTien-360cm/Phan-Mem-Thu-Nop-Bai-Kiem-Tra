using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class RealtimeService(
    string baseUrl,
    RealtimeAuthenticationMode authenticationMode = RealtimeAuthenticationMode.AccountBearer)
    : IRealtimeService, IAsyncDisposable
{
    private readonly RealtimeSessionSubscriptions subscriptions = new();
    private readonly StudentNotificationRealtimeAdapter studentNotifications = new();
    private HubConnection? hub;

    public bool IsConnected => hub?.State == HubConnectionState.Connected;

    public event EventHandler<string>? EventReceived;
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

    public async Task ConnectAsync(string? token = null, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return;
        }

        if (hub is not null)
        {
            await hub.DisposeAsync();
        }

        var connection = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + ContractInfo.HubPath, options =>
            {
                ConfigureAuthentication(options, token, authenticationMode);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            })
            .Build();
        hub = connection;

        var studentEventNames = Enum.GetValues<StudentNotificationEventType>()
            .Select(value => value.ToString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var eventName in studentEventNames)
        {
            connection.On<JsonElement>(
                eventName,
                envelope =>
                {
                    if (!studentNotifications.TryAccept(envelope, out var notification)
                        || notification is null)
                        return;
                    NotificationReceived?.Invoke(
                        this,
                        new StudentRealtimeNotification(
                            notification.SessionId,
                            notification.EventType.ToString(),
                            notification.Revision,
                            null,
                            notification.ParticipantId,
                            null,
                            notification));
                    EventReceived?.Invoke(this, notification.EventType.ToString());
                });
        }

        connection.On<RealtimeEnvelope<TimeExtendedEvent>>(
            RealtimeEvents.TimeExtended,
            envelope =>
            {
                var payload = envelope.Payload with
                {
                    ServerNowUtc = envelope.Payload.ServerNowUtc ?? envelope.OccurredAtUtc,
                    Revision = envelope.Payload.Revision ?? envelope.Sequence
                };
                NotificationReceived?.Invoke(
                    this,
                    new(
                        envelope.SessionId,
                        RealtimeEvents.TimeExtended,
                        envelope.Sequence,
                        payload));
            });

        connection.On<RealtimeEnvelope<PublicCloudProjectionUpdatedEvent>>(
            RealtimeEvents.PublicCloudProjectionUpdated,
            envelope =>
            {
                NotificationReceived?.Invoke(
                    this,
                    new(
                        envelope.SessionId,
                        RealtimeEvents.PublicCloudProjectionUpdated,
                        envelope.Payload.ProjectionVersion,
                        null,
                        null,
                        envelope.Payload));
                EventReceived?.Invoke(this, RealtimeEvents.PublicCloudProjectionUpdated);
            });

        foreach (var eventName in typeof(RealtimeEvents)
                     .GetFields()
                     .Select(field => field.GetValue(null)?.ToString())
                     .Where(value => !string.IsNullOrWhiteSpace(value)
                          && value != RealtimeEvents.TimeExtended
                          && value != RealtimeEvents.PublicCloudProjectionUpdated
                          && !studentEventNames.Contains(value!)))
        {
            connection.On<JsonElement>(eventName!, envelope =>
            {
                var sessionId = envelope.TryGetProperty("sessionId", out var sessionElement)
                    && sessionElement.TryGetGuid(out var parsedSessionId)
                    ? parsedSessionId
                    : Guid.Empty;
                var revision = envelope.TryGetProperty("sequence", out var sequenceElement)
                    && sequenceElement.TryGetInt64(out var parsedRevision)
                    ? parsedRevision
                    : 0;
                Guid? participantId = null;
                if (envelope.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("participantId", out var participantElement)
                    && participantElement.TryGetGuid(out var parsedParticipantId))
                    participantId = parsedParticipantId;
                NotificationReceived?.Invoke(
                    this,
                    new(
                        sessionId,
                        eventName!,
                        revision,
                        null,
                        participantId));
                EventReceived?.Invoke(this, eventName!);
            });
        }

        connection.Reconnecting += _ =>
        {
            EventReceived?.Invoke(this, "Reconnecting");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            try
            {
                await subscriptions.RestoreAsync(
                    (sessionId, cancellationToken) => connection.InvokeAsync(
                        "SubscribeSession",
                        sessionId,
                        cancellationToken),
                    CancellationToken.None);
                EventReceived?.Invoke(this, "Reconnected");
            }
            catch (Exception ex)
            {
                FrontendLogger.Log(ex, "RealtimeService.Resubscribe");
                EventReceived?.Invoke(this, "ResubscribeFailed");
            }
        };
        connection.Closed += _ =>
        {
            EventReceived?.Invoke(this, "Disconnected");
            return Task.CompletedTask;
        };

        await connection.StartAsync(ct);
        await subscriptions.RestoreAsync(
            (sessionId, cancellationToken) => connection.InvokeAsync(
                "SubscribeSession",
                sessionId,
                cancellationToken),
            ct);
        EventReceived?.Invoke(this, "Connected");
    }

    internal static void ConfigureAuthentication(
        Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options,
        string? token,
        RealtimeAuthenticationMode mode)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(token))
            return;

        if (mode == RealtimeAuthenticationMode.ParticipantHeader)
        {
            // The Local Server authenticates participant connections from this
            // header during SignalR negotiate as well as the WebSocket upgrade.
            options.Headers["X-Exam-Session-Token"] = token.Trim();
            return;
        }

        options.AccessTokenProvider = () => Task.FromResult<string?>(token.Trim());
    }

    public async Task SubscribeSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        var connection = hub;
        await subscriptions.SubscribeAsync(
            sessionId,
            connection?.State == HubConnectionState.Connected,
            (id, cancellationToken) => connection!.InvokeAsync(
                "SubscribeSession",
                id,
                cancellationToken),
            ct);
    }

    public async Task UnsubscribeSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var connection = hub;
        await subscriptions.UnsubscribeAsync(
            sessionId,
            connection?.State == HubConnectionState.Connected,
            (id, cancellationToken) => connection!.InvokeAsync(
                "UnsubscribeSession",
                id,
                cancellationToken),
            ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (hub is null)
        {
            return;
        }

        await hub.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (hub is not null)
        {
            await hub.DisposeAsync();
            hub = null;
        }
    }
}

public enum RealtimeAuthenticationMode
{
    AccountBearer,
    ParticipantHeader
}

internal sealed class RealtimeSessionSubscriptions
{
    private readonly object gate = new();
    private readonly HashSet<Guid> sessionIds = [];

    public bool Add(Guid sessionId)
    {
        lock (gate)
            return sessionIds.Add(sessionId);
    }

    public bool Remove(Guid sessionId)
    {
        lock (gate)
            return sessionIds.Remove(sessionId);
    }

    public async Task SubscribeAsync(
        Guid sessionId,
        bool isConnected,
        Func<Guid, CancellationToken, Task> subscribe,
        CancellationToken cancellationToken)
    {
        Add(sessionId);
        if (isConnected)
            await subscribe(sessionId, cancellationToken);
    }

    public async Task UnsubscribeAsync(
        Guid sessionId,
        bool isConnected,
        Func<Guid, CancellationToken, Task> unsubscribe,
        CancellationToken cancellationToken)
    {
        Remove(sessionId);
        if (isConnected)
            await unsubscribe(sessionId, cancellationToken);
    }

    public async Task RestoreAsync(
        Func<Guid, CancellationToken, Task> subscribe,
        CancellationToken cancellationToken)
    {
        Guid[] snapshot;
        lock (gate)
            snapshot = [.. sessionIds];

        foreach (var sessionId in snapshot)
            await subscribe(sessionId, cancellationToken);
    }
}
