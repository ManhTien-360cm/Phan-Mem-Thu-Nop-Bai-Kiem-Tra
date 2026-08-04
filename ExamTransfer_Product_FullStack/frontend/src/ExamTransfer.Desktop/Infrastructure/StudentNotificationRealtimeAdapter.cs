using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class StudentNotificationRealtimeAdapter
{
    private const int EventIdCapacity = 4096;
    private readonly object gate = new();
    private readonly HashSet<Guid> eventIds = [];
    private readonly Queue<Guid> eventIdOrder = [];
    private readonly Dictionary<(Guid SessionId, Guid? ParticipantId), long> revisions = [];

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public bool TryAccept(
        JsonElement envelopeJson,
        out StudentNotificationEventDto? notification)
    {
        try
        {
            var envelope = envelopeJson.Deserialize<RealtimeEnvelope<StudentNotificationEventDto>>(JsonOptions);
            return TryAccept(envelope, out notification);
        }
        catch (JsonException)
        {
            notification = null;
            return false;
        }
    }

    public bool TryAccept(
        RealtimeEnvelope<StudentNotificationEventDto>? envelope,
        out StudentNotificationEventDto? notification)
    {
        notification = null;
        if (envelope?.Payload is not { } candidate)
            return false;
        if (envelope.EventId != candidate.EventId
            || envelope.SessionId != candidate.SessionId
            || envelope.Sequence != candidate.Revision
            || envelope.OccurredAtUtc != candidate.OccurredAtUtc
            || !string.Equals(envelope.EventType, candidate.EventType.ToString(), StringComparison.Ordinal)
            || StudentNotificationEventValidator.Validate(candidate).Count != 0)
            return false;

        lock (gate)
        {
            if (eventIds.Contains(candidate.EventId))
                return false;
            var scope = (candidate.SessionId, candidate.ParticipantId);
            if (revisions.TryGetValue(scope, out var latestRevision)
                && candidate.Revision <= latestRevision)
                return false;

            revisions[scope] = candidate.Revision;
            eventIds.Add(candidate.EventId);
            eventIdOrder.Enqueue(candidate.EventId);
            while (eventIdOrder.Count > EventIdCapacity)
                eventIds.Remove(eventIdOrder.Dequeue());
        }

        notification = candidate;
        return true;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
