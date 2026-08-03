using ExamTransfer.Application;
using System.Text.Json;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Workers;

public sealed class CloudSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ExamTransferOptions> options,
    ICloudSyncSignal cloudSyncSignal,
    ILogger<CloudSyncWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly CloudOptions cloudOptions = options.Value.Cloud;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await cloudSyncSignal.WaitAsync(
                    TimeSpan.FromSeconds(
                        Math.Max(2, cloudOptions.WorkerIntervalSeconds)),
                    stoppingToken);

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<AppDbContext>();
                var cloud = scope.ServiceProvider
                    .GetRequiredService<ICloudAdapter>();

                if (!cloud.CanSynchronize || !await cloud.CheckHealthAsync(stoppingToken))
                    continue;

                var now = DateTimeOffset.UtcNow;
                var queueCandidates = await db.SyncQueueSet
                    .Where(x =>
                        (x.Status == SyncStatus.Pending
                            || x.Status == SyncStatus.Failed))
                    .ToListAsync(stoppingToken);
                var items = queueCandidates
                    .Where(x =>
                        (x.NextRetryAtUtc == null
                            || x.NextRetryAtUtc <= now)
                        && (x.LeaseUntilUtc == null
                            || x.LeaseUntilUtc < now))
                    .OrderBy(x => x.CreatedAtUtc)
                    .Take(Math.Clamp(
                        cloudOptions.WorkerBatchSize,
                        1,
                        100))
                    .ToList();

                foreach (var item in items)
                {
                    if (!CloudEntityOwnershipRegistry.MayPushToCloud(item.EntityType, item.PayloadJson)
                        || await IsCloudOwnedSourceProjectionAsync(db, item, stoppingToken))
                    {
                        // PublicCloud rows are authored in Supabase through RPCs.
                        // A cached SQLite snapshot must never merge-upsert over them.
                        item.Status = SyncStatus.Synced;
                        item.LastError = null;
                        item.LeaseUntilUtc = null;
                        item.NextRetryAtUtc = null;
                        item.CompletedAtUtc = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                        logger.LogDebug(
                            "Skipped LAN push for PublicCloud authority row {EntityType}/{EntityId}",
                            item.EntityType,
                            item.EntityId);
                        continue;
                    }

                    item.Status = SyncStatus.Syncing;
                    item.LeaseUntilUtc = now.AddMinutes(
                        Math.Max(2, cloudOptions.LeaseMinutes));
                    item.LastAttemptAtUtc = DateTimeOffset.UtcNow;
                    await MarkProjectionStatusAsync(
                        db,
                        item,
                        SyncStatus.Syncing,
                        null,
                        stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);

                    try
                    {
                        var result = await cloud.PushAsync(
                            item,
                            ct => db.SaveChangesAsync(ct),
                            stoppingToken);
                        item.Status = SyncStatus.Synced;
                        item.LastError = null;
                        item.LeaseUntilUtc = null;
                        item.NextRetryAtUtc = null;
                        item.CloudObjectPath = result.CloudObjectPath;
                        item.CompletedAtUtc = DateTimeOffset.UtcNow;
                        await MarkProjectionStatusAsync(
                            db,
                            item,
                            SyncStatus.Synced,
                            result.CloudObjectPath,
                            stoppingToken);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (ApiException ex)
                    {
                        var roomCodeConflict = string.Equals(
                            ex.Code,
                            ErrorCodes.RoomCodeConflict,
                            StringComparison.Ordinal);
                        item.Status = roomCodeConflict
                            ? SyncStatus.Conflict
                            : SyncStatus.Failed;
                        item.RetryCount++;
                        item.LastError = JsonSerializer.Serialize(
                            new CloudSyncFailure(ex.Code, ex.Message),
                            JsonOptions);
                        item.LeaseUntilUtc = null;
                        item.NextRetryAtUtc = roomCodeConflict
                            ? null
                            : DateTimeOffset.UtcNow.AddSeconds(
                                Math.Min(
                                    3600,
                                    Math.Pow(
                                        2,
                                        Math.Min(item.RetryCount, 10))));
                        item.CompletedAtUtc = null;
                        await MarkProjectionStatusAsync(
                            db,
                            item,
                            item.Status,
                            item.CloudObjectPath,
                            stoppingToken);
                        logger.LogWarning(
                            "Cloud sync rejected for {EntityType}/{EntityId}. Code={Code}; Status={Status}; RetryScheduled={RetryScheduled}",
                            item.EntityType,
                            item.EntityId,
                            ex.Code,
                            item.Status,
                            item.NextRetryAtUtc.HasValue);
                    }
                    catch (Exception ex)
                    {
                        item.Status = SyncStatus.Failed;
                        item.RetryCount++;
                        item.LastError = ex.Message;
                        item.LeaseUntilUtc = null;
                        item.NextRetryAtUtc =
                            DateTimeOffset.UtcNow.AddSeconds(
                                Math.Min(
                                    3600,
                                    Math.Pow(
                                        2,
                                        Math.Min(item.RetryCount, 10))));
                        await MarkProjectionStatusAsync(
                            db,
                            item,
                            SyncStatus.Failed,
                            item.CloudObjectPath,
                            stoppingToken);
                        logger.LogWarning(
                            ex,
                            "Cloud sync failed for {EntityType}/{EntityId}",
                            item.EntityType,
                            item.EntityId);
                    }

                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cloud sync worker failed");
            }
        }
    }

    private static async Task<bool> IsCloudOwnedSourceProjectionAsync(
        AppDbContext db,
        SyncQueueItem item,
        CancellationToken cancellationToken)
    {
        if (CloudEntityOwnershipRegistry.GetAuthority(item.EntityType)
            != CloudEntityAuthority.SourceModeDependent)
            return false;
        if (!Guid.TryParse(item.EntityId, out var id)) return false;
        return item.EntityType.Trim().ToLowerInvariant() switch
        {
            "participant" or "session_participant" or "session_participants" =>
                await db.SessionParticipantsSet.AnyAsync(x => x.Id == id && x.Session.AccessMode == SessionAccessMode.PublicCloud, cancellationToken),
            "submission" or "submissions" =>
                await db.SubmissionsSet.AnyAsync(x => x.Id == id && x.Session.AccessMode == SessionAccessMode.PublicCloud, cancellationToken),
            "submission_file" or "submission_files" =>
                await db.SubmissionFilesSet.AnyAsync(x => x.Id == id && x.Submission.Session.AccessMode == SessionAccessMode.PublicCloud, cancellationToken),
            "violation" or "violations" =>
                await db.ViolationsSet.AnyAsync(x => x.Id == id
                    && db.ExamSessionsSet.Any(s => s.Id == x.SessionId && s.AccessMode == SessionAccessMode.PublicCloud), cancellationToken),
            "quiz_attempt" or "quiz_attempts" =>
                await db.QuizAttemptsSet.AnyAsync(x => x.Id == id && x.Session.AccessMode == SessionAccessMode.PublicCloud, cancellationToken),
            "quiz_answer" or "quiz_answers" =>
                await db.QuizAnswersSet.AnyAsync(x => x.Id == id && x.Attempt.Session.AccessMode == SessionAccessMode.PublicCloud, cancellationToken),
            _ => false
        };
    }

    private static async Task MarkProjectionStatusAsync(
        AppDbContext db,
        SyncQueueItem item,
        SyncStatus status,
        string? cloudObjectPath,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(item.EntityId, out var id))
            return;

        switch (item.EntityType.Trim().ToLowerInvariant())
        {
            case "exam_file":
            case "exam_files":
            {
                var entity = await db.ExamFilesSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "submission_file":
            case "submission_files":
            {
                var entity = await db.SubmissionFilesSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "graded_attachment":
            case "graded_attachments":
            {
                var entity = await db.GradedAttachmentsSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "export_job":
            case "export_jobs":
            {
                var entity = await db.ExportJobsSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "backup":
            case "backups":
            {
                var entity = await db.BackupsSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
        }
    }
}
