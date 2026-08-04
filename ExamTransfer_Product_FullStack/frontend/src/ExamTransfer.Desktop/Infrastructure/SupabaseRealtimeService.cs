using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class SupabaseRealtimeService : IAsyncDisposable
{
    private static readonly int[] RetrySeconds = [1, 2, 5, 10, 30];
    private const int MaximumReconnectAttempts = 8;
    private readonly IPublicCloudRuntimeOptionsProvider optionsProvider;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly PublicCloudStudentNotificationTransport studentNotifications = new();
    private CancellationTokenSource? stopping;
    private ClientWebSocket? socket;
    private Task? loop;
    private Guid sessionId;
    private Guid participantId;
    private string deviceId = string.Empty;
    private ISupabaseAccessTokenProvider? accessTokenProvider;
    private Func<CancellationToken, Task>? refreshSnapshot;
    private Func<long, Guid?, int, CancellationToken,
        Task<IReadOnlyList<StudentNotificationEventDto>>>? fetchNotifications;
    private long reference;

    public bool IsConnected => socket?.State == WebSocketState.Open;
    public bool IsRunning => loop is { IsCompleted: false };
    public Guid? ActiveSessionId => IsRunning ? sessionId : null;
    public event EventHandler<string>? EventReceived;
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

    public SupabaseRealtimeService(
        IPublicCloudRuntimeOptionsProvider? optionsProvider = null)
    {
        this.optionsProvider = optionsProvider ?? new PublicCloudRuntimeOptionsProvider();
    }

    public Task StartAsync(
        Guid session,
        Guid participant,
        string device,
        ISupabaseAccessTokenProvider tokenProvider,
        Func<CancellationToken, Task> snapshotRefresh,
        Func<long, Guid?, int, CancellationToken,
            Task<IReadOnlyList<StudentNotificationEventDto>>> notificationCatchUp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        sessionId = session;
        participantId = participant;
        studentNotifications.SetScope(session, participant);
        deviceId = device;
        accessTokenProvider = tokenProvider;
        refreshSnapshot = snapshotRefresh;
        fetchNotifications = notificationCatchUp;
        stopping = new CancellationTokenSource();
        loop = RunAsync(stopping.Token);
        return Task.CompletedTask;
    }

    public async Task BroadcastTelemetryAsync(object payload, CancellationToken cancellationToken)
    {
        var topic = $"exam-session:{sessionId}:telemetry:{deviceId}";
        await SendAsync(topic, "broadcast", new
        {
            type = "broadcast",
            @event = "telemetry",
            payload,
            @private = true
        }, cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retry = 0;
        var connectedBefore = false;
        var forceRefresh = false;
        var authRefreshAttempted = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var options = optionsProvider.Get();
                if (!options.Configured)
                    throw new PublicCloudApiException(
                        options.ErrorCode ?? "PUBLICCLOUD_NOT_CONFIGURED",
                        "PublicCloud Realtime configuration is missing or invalid.",
                        System.Net.HttpStatusCode.ServiceUnavailable);
                var token = await ResolveAccessTokenForConnectionAsync(
                    forceRefresh,
                    cancellationToken);
                forceRefresh = false;
                socket?.Dispose();
                socket = new ClientWebSocket();
                var httpUrl = options.ProjectUri!;
                var scheme = httpUrl.Scheme == "https" ? "wss" : "ws";
                var endpoint = new Uri(
                    $"{scheme}://{httpUrl.Authority}/realtime/v1/websocket?apikey={Uri.EscapeDataString(options.PublishableKey!)}&vsn=1.0.0");
                await socket.ConnectAsync(endpoint, cancellationToken);
                foreach (var topic in new[]
                {
                    $"exam-session:{sessionId}",
                    $"exam-session:{sessionId}:device:{deviceId}",
                    $"exam-session:{sessionId}:telemetry:{deviceId}"
                })
                {
                    await SendAsync(topic, "phx_join", new
                    {
                        config = new
                        {
                            @private = true,
                            broadcast = new { self = false, ack = true },
                            presence = new { key = deviceId }
                        },
                        access_token = token
                    }, cancellationToken);
                }

                var bufferedFrames = await JoinStudentNotificationsAsync(
                    token,
                    cancellationToken);

                retry = 0;
                if (refreshSnapshot is not null)
                    await refreshSnapshot(cancellationToken);
                if (fetchNotifications is not null)
                    await studentNotifications.CatchUpAsync(
                        fetchNotifications,
                        PublishStudentNotification,
                        cancellationToken);
                foreach (var frame in bufferedFrames)
                    ProcessIncomingMessage(frame);
                if (connectedBefore)
                    EventReceived?.Invoke(this, "Reconnected");
                connectedBefore = true;
                await ReceiveUntilDisconnectedAsync(cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    throw new WebSocketException("Supabase Realtime connection closed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SupabaseRealtimeAuthException ex)
            {
                if (ex.Expired && !authRefreshAttempted)
                {
                    authRefreshAttempted = true;
                    forceRefresh = true;
                    continue;
                }
                EventReceived?.Invoke(this, "AuthenticationExpired");
                break;
            }
            catch (PublicCloudApiException ex) when (
                ex.Code is "PUBLICCLOUD_AUTH_EXPIRED" or "PUBLICCLOUD_AUTH_INVALID")
            {
                EventReceived?.Invoke(this, "AuthenticationExpired");
                break;
            }
            catch
            {
                EventReceived?.Invoke(this, "Reconnecting");
                if (retry >= MaximumReconnectAttempts)
                {
                    EventReceived?.Invoke(this, "Disconnected");
                    break;
                }
                var delay = RetrySeconds[Math.Min(retry++, RetrySeconds.Length - 1)];
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            }
        }
    }

    public Task<string> ResolveAccessTokenForConnectionAsync(
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        (accessTokenProvider
            ?? throw new InvalidOperationException("Supabase access-token provider is missing."))
        .GetValidAccessTokenAsync(forceRefresh, cancellationToken);

    private async Task ReceiveUntilDisconnectedAsync(CancellationToken cancellationToken)
    {
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoopAsync(receiveCts.Token);
        try
        {
            var buffer = new byte[32 * 1024];
            while (socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                if (TryParseAuthFailure(json, out var expired))
                    throw new SupabaseRealtimeAuthException(expired);
                ProcessIncomingMessage(json);
            }
        }
        finally
        {
            receiveCts.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (receiveCts.IsCancellationRequested) { }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await SendFrameAsync("phoenix", "heartbeat", new { }, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> JoinStudentNotificationsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var bufferedFrames = new List<string>();
        var topic = $"realtime:student-notifications:{sessionId}:{participantId}";
        var joinReference = await SendFrameAsync(
            topic,
            "phx_join",
            new
            {
                config = new
                {
                    broadcast = new { self = false, ack = false },
                    presence = new { enabled = false },
                    postgres_changes = new[]
                    {
                        new
                        {
                            @event = "INSERT",
                            schema = "public",
                            table = "student_notification_events",
                            filter = $"session_id=eq.{sessionId}"
                        }
                    }
                },
                access_token = accessToken
            },
            cancellationToken);

        while (true)
        {
            var json = await ReceiveTextMessageAsync(cancellationToken);
            if (TryParseAuthFailure(json, out var expired))
                throw new SupabaseRealtimeAuthException(expired);
            if (TryParseJoinReply(json, topic, joinReference, out var accepted))
            {
                if (!accepted)
                    throw new WebSocketException("Supabase rejected the student notification subscription.");
                return bufferedFrames;
            }
            bufferedFrames.Add(json);
        }
    }

    private async Task<string> ReceiveTextMessageAsync(CancellationToken cancellationToken)
    {
        var activeSocket = socket
            ?? throw new WebSocketException("Supabase Realtime socket is missing.");
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await activeSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Supabase Realtime connection closed during subscription.");
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    private void ProcessIncomingMessage(string json)
    {
        if (studentNotifications.TryAcceptRealtime(json, out var studentNotification)
            && studentNotification is not null)
        {
            PublishStudentNotification(studentNotification);
            return;
        }
        if (!TryParseBroadcast(
                json,
                sessionId,
                deviceId,
                out var notification,
                out var eventName))
            return;
        if (notification is not null)
            NotificationReceived?.Invoke(this, notification);
        else if (eventName is not null)
            EventReceived?.Invoke(this, eventName);
    }

    private void PublishStudentNotification(StudentRealtimeNotification notification)
    {
        NotificationReceived?.Invoke(this, notification);
        EventReceived?.Invoke(this, notification.EventName);
    }

    public static bool TryParseJoinReply(
        string json,
        string expectedTopic,
        string expectedReference,
        out bool accepted)
    {
        accepted = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? topic;
            string? eventName;
            string? reference;
            JsonElement payload;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 5)
            {
                reference = root[1].GetString();
                topic = root[2].GetString();
                eventName = root[3].GetString();
                payload = root[4];
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("topic", out var topicElement)
                     && root.TryGetProperty("event", out var eventElement)
                     && root.TryGetProperty("ref", out var referenceElement)
                     && root.TryGetProperty("payload", out payload))
            {
                topic = topicElement.GetString();
                eventName = eventElement.GetString();
                reference = referenceElement.GetString();
            }
            else
            {
                return false;
            }
            if (topic != expectedTopic
                || eventName != "phx_reply"
                || reference != expectedReference
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("status", out var status))
                return false;
            accepted = status.GetString() == "ok";
            return true;
        }
        catch (JsonException)
        {
            accepted = false;
            return false;
        }
    }

    public static bool TryParseBroadcast(
        string json,
        Guid expectedSessionId,
        string expectedDeviceId,
        out StudentRealtimeNotification? notification,
        out string? eventName)
    {
        notification = null;
        eventName = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? topic;
            string? outerEvent;
            JsonElement outerPayload;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 5)
            {
                topic = root[2].GetString();
                outerEvent = root[3].GetString();
                outerPayload = root[4];
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("topic", out var topicElement)
                     && root.TryGetProperty("event", out var eventElement)
                     && root.TryGetProperty("payload", out outerPayload))
            {
                topic = topicElement.GetString();
                outerEvent = eventElement.GetString();
            }
            else
            {
                return false;
            }

            var sessionTopic = $"realtime:exam-session:{expectedSessionId}";
            var deviceTopic = $"{sessionTopic}:device:{expectedDeviceId}";
            var telemetryTopic = $"{sessionTopic}:telemetry:{expectedDeviceId}";
            if (!string.Equals(outerEvent, "broadcast", StringComparison.Ordinal)
                || topic is null
                || (topic != sessionTopic
                    && topic != deviceTopic
                    && topic != telemetryTopic))
                return false;
            if (outerPayload.ValueKind != JsonValueKind.Object
                || !outerPayload.TryGetProperty("event", out var innerEventElement))
                return false;

            eventName = innerEventElement.GetString();
            if (eventName is RealtimeEvents.QuizGradeReturned
                or RealtimeEvents.QuizGradeReopened)
            {
                if (topic != deviceTopic
                    || !outerPayload.TryGetProperty("meta", out var gradeMetadata)
                    || gradeMetadata.ValueKind != JsonValueKind.Object
                    || !TryGuid(gradeMetadata, "id", out var metadataId)
                    || !outerPayload.TryGetProperty("payload", out var gradePayload)
                    || !HasExactGradeSignalKeys(gradePayload, metadataId)
                    || !gradePayload.TryGetProperty("eventType", out var eventType)
                    || eventType.GetString() != eventName
                    || !TryGuid(gradePayload, "attemptId", out var gradeAttemptId)
                    || !TryGuid(gradePayload, "sessionId", out var gradeSessionId)
                    || gradeSessionId != expectedSessionId)
                {
                    notification = null;
                    eventName = null;
                    return false;
                }
                eventName = $"{eventName}:{gradeAttemptId:N}";
                return true;
            }

            if (!string.Equals(
                    eventName,
                    RealtimeEvents.TimeExtended,
                    StringComparison.Ordinal))
                return !string.IsNullOrWhiteSpace(eventName);

            if (!outerPayload.TryGetProperty("payload", out var payload)
                || !TryGuid(payload, "participantId", out var participantId)
                || !TryDate(payload, "serverNowUtc", out var serverNowUtc)
                || !TryInt64(payload, "revision", out var revision)
                || !(TryDate(payload, "effectiveDeadlineUtc", out var deadline)
                     || TryDate(payload, "effectiveDeadline", out deadline)))
            {
                notification = null;
                eventName = RealtimeEvents.TimeExtended;
                return true;
            }

            _ = TryGuid(payload, "attemptId", out var attemptId);
            _ = TryGuid(payload, "requestId", out var requestId);
            var minutes = payload.TryGetProperty("minutes", out var minutesElement)
                && minutesElement.TryGetInt32(out var parsedMinutes)
                ? parsedMinutes
                : 0;
            var timeExtended = new TimeExtendedEvent(
                participantId,
                minutes,
                deadline,
                attemptId == Guid.Empty ? null : attemptId,
                serverNowUtc,
                revision,
                requestId == Guid.Empty ? null : requestId);
            notification = new(
                expectedSessionId,
                RealtimeEvents.TimeExtended,
                revision,
                timeExtended);
            eventName = null;
            return true;
        }
        catch (JsonException)
        {
            notification = null;
            eventName = null;
            return false;
        }
    }

    private static bool HasExactGradeSignalKeys(
        JsonElement payload,
        Guid metadataId)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;
        var count = 0;
        foreach (var property in payload.EnumerateObject())
        {
            count++;
            if (property.Name is not ("id" or "eventType" or "attemptId" or "sessionId"))
                return false;
        }
        return count == 4
            && TryGuid(payload, "id", out var payloadId)
            && payloadId == metadataId;
    }

    private static bool TryGuid(JsonElement value, string name, out Guid parsed)
    {
        parsed = Guid.Empty;
        return value.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetGuid(out parsed);
    }

    private static bool TryDate(JsonElement value, string name, out DateTimeOffset parsed)
    {
        parsed = default;
        return value.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetDateTimeOffset(out parsed);
    }

    private static bool TryInt64(JsonElement value, string name, out long parsed)
    {
        parsed = default;
        return value.TryGetProperty(name, out var element)
            && element.TryGetInt64(out parsed);
    }

    private Task SendAsync(
        string topic,
        string eventName,
        object payload,
        CancellationToken cancellationToken) =>
        SendFrameAsync("realtime:" + topic, eventName, payload, cancellationToken);

    private async Task<string> SendFrameAsync(
        string topic,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        if (socket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Supabase Realtime is not connected.");
        var nextReference = Interlocked.Increment(ref reference).ToString();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            topic,
            @event = eventName,
            payload,
            @ref = nextReference
        });
        await sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendGate.Release(); }
        return nextReference;
    }

    public void Stop()
    {
        stopping?.Cancel();
        stopping?.Dispose();
        stopping = null;
        socket?.Abort();
        socket?.Dispose();
        socket = null;
        loop = null;
        accessTokenProvider = null;
        refreshSnapshot = null;
        fetchNotifications = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool TryParseAuthFailure(string json, out bool expired)
    {
        expired = false;
        if (!json.Contains("Token has expired", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("InvalidJWTExpiration", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("MalformedJWT", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("JwtSignatureError", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return false;
        expired = json.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || json.Contains("InvalidJWTExpiration", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private sealed class SupabaseRealtimeAuthException(bool expired) : Exception
    {
        public bool Expired { get; } = expired;
    }
}
