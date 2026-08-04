using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class PublicCloudStudentNotificationTransport
{
    public const int CatchUpPageSize = 100;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private StudentNotificationRealtimeAdapter adapter = new();
    private Guid sessionId;
    private Guid participantId;
    private long catchUpRevision;
    private Guid? catchUpEventId;

    public long CatchUpRevision => catchUpRevision;
    public Guid? CatchUpEventId => catchUpEventId;

    public void SetScope(Guid session, Guid participant)
    {
        if (session == Guid.Empty || participant == Guid.Empty)
            throw new ArgumentException("PublicCloud notification scope requires session and participant ids.");
        if (sessionId == session && participantId == participant)
            return;
        sessionId = session;
        participantId = participant;
        catchUpRevision = 0;
        catchUpEventId = null;
        adapter = new StudentNotificationRealtimeAdapter();
    }

    public bool TryAcceptRealtime(
        string json,
        out StudentRealtimeNotification? notification)
    {
        notification = null;
        if (!TryParseInsert(json, out var candidate)
            || candidate is null
            || !IsInScope(candidate)
            || !adapter.TryAccept(Envelope(candidate), out var accepted)
            || accepted is null)
            return false;
        notification = ToRealtime(accepted);
        return true;
    }

    public async Task CatchUpAsync(
        Func<long, Guid?, int, CancellationToken,
            Task<IReadOnlyList<StudentNotificationEventDto>>> fetch,
        Action<StudentRealtimeNotification> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(publish);
        while (true)
        {
            var page = await fetch(
                catchUpRevision,
                catchUpEventId,
                CatchUpPageSize,
                cancellationToken);
            if (page.Count > CatchUpPageSize)
                throw new InvalidDataException("PublicCloud notification catch-up exceeded its page limit.");

            long previousRevision = catchUpRevision;
            Guid? previousEventId = catchUpEventId;
            foreach (var candidate in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsInScope(candidate)
                    || StudentNotificationEventValidator.Validate(candidate).Count != 0
                    || candidate.Revision < previousRevision
                    || candidate.Revision == previousRevision
                      && (!previousEventId.HasValue || candidate.EventId == previousEventId.Value))
                    throw new InvalidDataException("PublicCloud notification catch-up order or scope is invalid.");

                if (adapter.TryAccept(Envelope(candidate), out var accepted)
                    && accepted is not null)
                    publish(ToRealtime(accepted));

                previousRevision = candidate.Revision;
                previousEventId = candidate.EventId;
                catchUpRevision = previousRevision;
                catchUpEventId = previousEventId;
            }
            if (page.Count < CatchUpPageSize)
                return;
        }
    }

    public static bool TryParseInsert(
        string json,
        out StudentNotificationEventDto? notification)
    {
        notification = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement outerPayload;
            string? outerEvent;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 5)
            {
                outerEvent = root[3].GetString();
                outerPayload = root[4];
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("event", out var eventElement)
                     && root.TryGetProperty("payload", out outerPayload))
            {
                outerEvent = eventElement.GetString();
            }
            else
            {
                return false;
            }
            if (!string.Equals(outerEvent, "postgres_changes", StringComparison.Ordinal)
                || outerPayload.ValueKind != JsonValueKind.Object
                || !outerPayload.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || data.GetProperty("type").GetString() != "INSERT"
                || data.GetProperty("schema").GetString() != "public"
                || data.GetProperty("table").GetString() != "student_notification_events"
                || !data.TryGetProperty("record", out var row)
                || row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("payload", out var payload))
                return false;

            var candidate = payload.Deserialize<StudentNotificationEventDto>(Json);
            if (candidate is null
                || StudentNotificationEventValidator.Validate(candidate).Count != 0
                || !TryGuid(row, "id", out var eventId)
                || !TryGuid(row, "session_id", out var rowSessionId)
                || !TryInt64(row, "revision", out var revision)
                || !TryDate(row, "occurred_at", out var occurredAt)
                || !row.TryGetProperty("event_type", out var eventType)
                || eventId != candidate.EventId
                || rowSessionId != candidate.SessionId
                || revision != candidate.Revision
                || occurredAt != candidate.OccurredAtUtc
                || eventType.GetString() != candidate.EventType.ToString())
                return false;
            Guid? rowParticipantId = null;
            if (row.TryGetProperty("participant_id", out var participantElement)
                && participantElement.ValueKind != JsonValueKind.Null)
            {
                if (!participantElement.TryGetGuid(out var parsedParticipantId))
                    return false;
                rowParticipantId = parsedParticipantId;
            }
            if (rowParticipantId != candidate.ParticipantId)
                return false;
            notification = candidate;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            notification = null;
            return false;
        }
    }

    private bool IsInScope(StudentNotificationEventDto candidate) =>
        candidate.SessionId == sessionId
        && (candidate.ParticipantId is null || candidate.ParticipantId == participantId);

    private static RealtimeEnvelope<StudentNotificationEventDto> Envelope(
        StudentNotificationEventDto value) =>
        new(
            value.EventId,
            value.SessionId,
            value.Revision,
            value.OccurredAtUtc,
            value.EventType.ToString(),
            value);

    private static StudentRealtimeNotification ToRealtime(
        StudentNotificationEventDto value) =>
        new(
            value.SessionId,
            value.EventType.ToString(),
            value.Revision,
            null,
            value.ParticipantId,
            null,
            value);

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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
