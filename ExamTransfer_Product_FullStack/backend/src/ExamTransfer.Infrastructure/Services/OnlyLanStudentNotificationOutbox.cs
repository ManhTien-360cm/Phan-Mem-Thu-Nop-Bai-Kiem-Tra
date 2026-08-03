using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamTransfer.Infrastructure.Services;

public static class OnlyLanStudentNotificationOutbox
{
    public const string EntityType = "onlylan_student_notifications";
    public const int ReplayLimit = 100;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static StudentNotificationEventDto Enqueue(
        AppDbContext db,
        StudentNotificationEventType eventType,
        Guid sessionId,
        long revision,
        Guid? participantId = null,
        Guid? submissionId = null,
        Guid? attemptId = null,
        string? message = null,
        string? reason = null,
        decimal? score = null,
        decimal? maxScore = null,
        DateTimeOffset? occurredAtUtc = null,
        Guid? eventId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var resolvedEventId = eventId ?? Guid.NewGuid();
        var notification = new StudentNotificationEventDto
        {
            EventId = resolvedEventId,
            EventType = eventType,
            SessionId = sessionId,
            ParticipantId = participantId,
            SubmissionId = submissionId,
            AttemptId = attemptId,
            Message = Normalize(message),
            Reason = Normalize(reason),
            Score = score,
            MaxScore = maxScore,
            OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
            Revision = revision
        };
        StudentNotificationEventValidator.EnsureValid(notification);

        db.SyncQueueSet.Add(new SyncQueueItem
        {
            Id = resolvedEventId,
            EntityType = EntityType,
            EntityId = resolvedEventId.ToString("N"),
            Operation = participantId.HasValue ? "participant" : "session",
            PayloadJson = JsonSerializer.Serialize(notification, JsonOptions),
            Status = SyncStatus.LocalOnly,
            NextRetryAtUtc = DateTimeOffset.UtcNow
        });
        return notification;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class OnlyLanStudentNotificationDispatcher(
    AppDbContext db,
    IOnlyLanStudentNotificationTransport transport,
    ILogger<OnlyLanStudentNotificationDispatcher> logger)
{
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var itemCandidates = await db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType
                && (x.Status == SyncStatus.LocalOnly
                    || x.Status == SyncStatus.Syncing))
            .ToListAsync(cancellationToken);
        var items = itemCandidates
            .Where(x => (x.Status == SyncStatus.LocalOnly
                    || x.LeaseUntilUtc == null
                    || x.LeaseUntilUtc < now)
                && (x.NextRetryAtUtc == null || x.NextRetryAtUtc <= now))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(100)
            .ToList();

        var delivered = 0;
        foreach (var item in items)
        {
            item.Status = SyncStatus.Syncing;
            item.LeaseUntilUtc = now.AddMinutes(2);
            item.LastAttemptAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var notification = DeserializeAndValidate(item);
                var accessMode = await db.ExamSessionsSet
                    .AsNoTracking()
                    .Where(x => x.Id == notification.SessionId)
                    .Select(x => (SessionAccessMode?)x.AccessMode)
                    .SingleOrDefaultAsync(cancellationToken);
                if (accessMode != SessionAccessMode.LanOnly)
                    throw new InvalidOperationException("ONLYLAN_REALTIME_SESSION_SCOPE_INVALID");

                if (notification.ParticipantId.HasValue)
                {
                    var ownsRoute = await db.SessionParticipantsSet
                        .AsNoTracking()
                        .AnyAsync(x => x.Id == notification.ParticipantId.Value
                            && x.SessionId == notification.SessionId,
                            cancellationToken);
                    if (!ownsRoute)
                        throw new InvalidOperationException("ONLYLAN_REALTIME_PARTICIPANT_SCOPE_INVALID");
                    await transport.PublishParticipantAsync(notification, cancellationToken);
                }
                else
                {
                    await transport.PublishSessionAsync(notification, cancellationToken);
                }

                item.Status = SyncStatus.Synced;
                item.LastError = null;
                item.LeaseUntilUtc = null;
                item.NextRetryAtUtc = null;
                item.CompletedAtUtc = DateTimeOffset.UtcNow;
                delivered++;
                logger.LogInformation(
                    "OnlyLAN student notification delivered. EventId={EventId}; EventType={EventType}; SessionId={SessionId}; ParticipantId={ParticipantId}; Revision={Revision}; RetryCount={RetryCount}",
                    notification.EventId,
                    notification.EventType,
                    notification.SessionId,
                    notification.ParticipantId,
                    notification.Revision,
                    item.RetryCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Status = IsPermanent(ex) ? SyncStatus.Conflict : SyncStatus.LocalOnly;
                item.RetryCount++;
                item.LastError = Sanitize(ex);
                item.LeaseUntilUtc = null;
                item.NextRetryAtUtc = item.Status == SyncStatus.LocalOnly
                    ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(item.RetryCount, 8))))
                    : null;
                logger.LogWarning(
                    "OnlyLAN student notification delivery failed. EventId={EventId}; EntityId={EntityId}; RetryCount={RetryCount}; ErrorCode={ErrorCode}",
                    item.Id,
                    item.EntityId,
                    item.RetryCount,
                    item.LastError);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await DeleteExpiredAsync(now, cancellationToken);
        return delivered;
    }

    public async Task<int> ReplayAsync(
        Guid sessionId,
        Guid participantId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || participantId == Guid.Empty || string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A validated replay scope is required.");

        var cutoff = DateTimeOffset.UtcNow - OnlyLanStudentNotificationOutbox.Retention;
        var storedCandidates = await db.SyncQueueSet
            .AsNoTracking()
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType
                && x.Status == SyncStatus.Synced)
            .ToListAsync(cancellationToken);
        var candidates = storedCandidates
            .Where(x => x.CompletedAtUtc >= cutoff)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(OnlyLanStudentNotificationOutbox.ReplayLimit * 4)
            .ToList();

        var replay = candidates
            .Select(TryDeserialize)
            .Where(x => x is not null
                && x.SessionId == sessionId
                && (!x.ParticipantId.HasValue || x.ParticipantId == participantId))
            .OrderBy(x => x!.Revision)
            .ThenBy(x => x!.OccurredAtUtc)
            .TakeLast(OnlyLanStudentNotificationOutbox.ReplayLimit)
            .ToList();
        foreach (var notification in replay)
            await transport.PublishConnectionAsync(connectionId, notification!, cancellationToken);
        return replay.Count;
    }

    private static StudentNotificationEventDto DeserializeAndValidate(SyncQueueItem item)
    {
        var notification = JsonSerializer.Deserialize<StudentNotificationEventDto>(
            item.PayloadJson,
            OnlyLanStudentNotificationOutbox.JsonOptions)
            ?? throw new JsonException("ONLYLAN_REALTIME_PAYLOAD_NULL");
        StudentNotificationEventValidator.EnsureValid(notification);
        if (notification.EventId != item.Id
            || !string.Equals(item.EntityId, item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new JsonException("ONLYLAN_REALTIME_EVENT_ID_MISMATCH");
        if ((item.Operation == "participant") != notification.ParticipantId.HasValue)
            throw new JsonException("ONLYLAN_REALTIME_ROUTE_MISMATCH");
        return notification;
    }

    private static StudentNotificationEventDto? TryDeserialize(SyncQueueItem item)
    {
        try { return DeserializeAndValidate(item); }
        catch { return null; }
    }

    private async Task DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - OnlyLanStudentNotificationOutbox.Retention;
        var delivered = await db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType
                && x.Status == SyncStatus.Synced)
            .ToListAsync(cancellationToken);
        var expired = delivered
            .Where(x => x.CompletedAtUtc < cutoff)
            .Take(100)
            .ToList();
        if (expired.Count == 0) return;
        db.SyncQueueSet.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsPermanent(Exception ex) =>
        ex is JsonException or ArgumentException
        || ex.Message.StartsWith("ONLYLAN_REALTIME_", StringComparison.Ordinal);

    private static string Sanitize(Exception ex) => ex switch
    {
        JsonException => "PAYLOAD_INVALID",
        ArgumentException => "CONTRACT_INVALID",
        InvalidOperationException when ex.Message.StartsWith("ONLYLAN_REALTIME_", StringComparison.Ordinal) => ex.Message,
        _ => "TRANSPORT_FAILED"
    };
}
