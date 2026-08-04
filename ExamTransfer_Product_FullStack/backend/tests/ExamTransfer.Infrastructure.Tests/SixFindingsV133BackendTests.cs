using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer;
using ExamTransfer.LocalServer.Controllers;
using ExamTransfer.LocalServer.Discovery;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class SixFindingsV133BackendTests
{
    [Fact]
    public void PublicCloudRoomCodeRecovery_RequiresTeacherOrAdminPolicy()
    {
        var method = typeof(SessionsController).GetMethod(
            nameof(SessionsController.ChangePublicCloudRoomCode));

        var authorize = Assert.Single(method!.GetCustomAttributes(
            typeof(AuthorizeAttribute),
            inherit: true).Cast<AuthorizeAttribute>());
        Assert.Equal("TeacherOrAdmin", authorize.Policy);
    }

    [Fact]
    public async Task CloudSyncSignal_CoalescesBurstAndKeepsPeriodicTimeout()
    {
        var signal = new CloudSyncSignal();
        signal.Pulse();
        signal.Pulse();
        signal.Pulse();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), default));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(30), default));
    }

    [Fact]
    public async Task PublicCreate_WakesWorkerAndBecomesExplicitlyShareReady()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var signal = new CloudSyncSignal();
        var cloud = new SuccessfulPushCloud();
        var options = Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true,
                WorkerIntervalSeconds = 30,
                WorkerBatchSize = 10
            }
        });
        var service = CreateSessionService(
            database.Context,
            cloud,
            signal,
            options);
        var detail = await service.CreateAndOpenAsync(
            Request(exam.Id),
            "teacher-device",
            default);

        var pending = await service.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.False(pending.Ready);
        Assert.Equal(SyncStatus.Pending, pending.Status);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={database.Path}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        await using var provider = services.BuildServiceProvider();
        var worker = new CloudSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            signal,
            NullLogger<CloudSyncWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.AnyAsync(
                    item => item.EntityType == "exam_sessions"
                        && item.EntityId == detail.Summary.Id.ToString()
                        && item.Status == SyncStatus.Synced);
            });
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
        }

        await using var readinessContext = database.CreateContext();
        var readinessService = CreateSessionService(
            readinessContext,
            cloud,
            signal,
            options);
        var ready = await readinessService.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.True(ready.Ready);
        Assert.Equal(SyncStatus.Synced, ready.Status);
        Assert.Equal("PUBLICCLOUD_PROJECTION_READY", ready.Code);

        var sessionProjection = Assert.Single(
            await readinessContext.SyncQueueSet
                .Where(item => item.EntityType == "exam_sessions"
                    && item.EntityId == detail.Summary.Id.ToString())
                .ToListAsync());
        Assert.Equal(SyncStatus.Synced, sessionProjection.Status);
        Assert.Equal(0, sessionProjection.RetryCount);

        var auditItems = await readinessContext.SyncQueueSet
            .Where(item => item.EntityType == "audit_logs")
            .ToListAsync();
        Assert.Equal(2, auditItems.Count);
        Assert.Contains(
            auditItems,
            item => item.PayloadJson.Contains(
                "\"action\":\"SessionCreated\"",
                StringComparison.Ordinal));
        Assert.Contains(
            auditItems,
            item => item.PayloadJson.Contains(
                "\"action\":\"SessionStateChanged\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionFailureAndRetry_PreserveLocalWaitingAndSingleOutbox()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var signal = new CloudSyncSignal();
        var options = Options.Create(new ExamTransferOptions());
        var service = CreateSessionService(
            database.Context,
            new SuccessfulPushCloud(),
            signal,
            options);
        var detail = await service.CreateAndOpenAsync(
            Request(exam.Id),
            "teacher-device",
            default);
        var item = await database.Context.SyncQueueSet.SingleAsync(
            row => row.EntityType == "exam_sessions"
                && row.EntityId == detail.Summary.Id.ToString());
        item.Status = SyncStatus.Failed;
        item.LastError = "simulated transport failure";
        item.RetryCount = 2;
        await database.Context.SaveChangesAsync();

        var failed = await service.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.False(failed.Ready);
        Assert.Equal("PUBLICCLOUD_PROJECTION_FAILED", failed.Code);
        Assert.DoesNotContain("simulated", failed.Message, StringComparison.OrdinalIgnoreCase);

        var retrying = await service.RetryProjectionAsync(
            detail.Summary.Id,
            default);
        Assert.Equal(SyncStatus.Pending, retrying.Status);
        Assert.False(retrying.Ready);
        Assert.Equal(
            1,
            await database.Context.SyncQueueSet.CountAsync(
                row => row.EntityType == "exam_sessions"
                    && row.EntityId == detail.Summary.Id.ToString()));
        var local = await database.Context.ExamSessionsSet.SingleAsync(
            row => row.Id == detail.Summary.Id);
        Assert.Equal(SessionStatus.Waiting, local.Status);
        Assert.Equal(SessionAccessMode.PublicCloud, local.AccessMode);
    }

    [Fact]
    public async Task PublicRoomProjectionConflict_BecomesTypedConflictAndIsNotRetriedWithSameCode()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var signal = new CloudSyncSignal();
        var cloud = new RoomConflictPushCloud();
        var options = Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true,
                WorkerIntervalSeconds = 30,
                WorkerBatchSize = 10
            }
        });
        var service = CreateSessionService(
            database.Context,
            cloud,
            signal,
            options);
        var detail = await service.CreateAndOpenAsync(
            Request(exam.Id),
            "teacher-device",
            default);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={database.Path}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        await using var provider = services.BuildServiceProvider();
        var worker = new CloudSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            signal,
            NullLogger<CloudSyncWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.AnyAsync(
                    item => item.EntityType == "exam_sessions"
                        && item.EntityId == detail.Summary.Id.ToString()
                        && item.Status == SyncStatus.Conflict);
            });
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
        }

        await using var assertionContext = database.CreateContext();
        var projection = Assert.Single(await assertionContext.SyncQueueSet
            .Where(item => item.EntityType == "exam_sessions"
                && item.EntityId == detail.Summary.Id.ToString())
            .ToListAsync());
        Assert.Equal(SyncStatus.Conflict, projection.Status);
        Assert.NotEqual(SyncStatus.Synced, projection.Status);
        Assert.Equal(1, projection.RetryCount);
        var typedFailure = JsonSerializer.Deserialize<CloudSyncFailure>(
            projection.LastError!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(typedFailure);
        Assert.Equal(ErrorCodes.RoomCodeConflict, typedFailure.Code);
        Assert.Contains("Mã phòng PublicCloud", typedFailure.Message, StringComparison.Ordinal);
        Assert.Null(projection.CompletedAtUtc);
        Assert.Null(projection.NextRetryAtUtc);
        Assert.Equal(1, cloud.SessionPushCount);

        var readinessService = CreateSessionService(
            assertionContext,
            cloud,
            signal,
            options);
        var readiness = await readinessService.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.Equal(SyncStatus.Conflict, readiness.Status);
        Assert.Equal(ErrorCodes.RoomCodeConflict, readiness.Code);
        Assert.Contains("mã khác", readiness.Message, StringComparison.OrdinalIgnoreCase);

        var sameCodeRetry = await readinessService.RetryProjectionAsync(
            detail.Summary.Id,
            default);
        Assert.Equal(SyncStatus.Conflict, sameCodeRetry.Status);
        assertionContext.ChangeTracker.Clear();
        var unchanged = await assertionContext.SyncQueueSet.SingleAsync(
            item => item.EntityType == "exam_sessions"
                && item.EntityId == detail.Summary.Id.ToString());
        Assert.Equal(SyncStatus.Conflict, unchanged.Status);
        Assert.Null(unchanged.NextRetryAtUtc);
        Assert.Equal(1, unchanged.RetryCount);
    }

    [Fact]
    public async Task PublicRoomCodeRecovery_ReusesSessionAndQueueThenBecomesReady()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var signal = new CloudSyncSignal();
        var cloud = new RecoverableRoomConflictPushCloud();
        var options = Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true,
                WorkerIntervalSeconds = 30,
                WorkerBatchSize = 10
            }
        });
        var service = CreateSessionService(database.Context, cloud, signal, options);
        var created = await service.CreateAndOpenAsync(
            Request(exam.Id),
            "teacher-device",
            default);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={database.Path}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        await using (var provider = services.BuildServiceProvider())
        {
            var worker = new CloudSyncWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                options,
                signal,
                NullLogger<CloudSyncWorker>.Instance);
            await worker.StartAsync(default);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    await using var verify = database.CreateContext();
                    return await verify.SyncQueueSet.AnyAsync(
                        item => item.EntityType == "exam_sessions"
                            && item.EntityId == created.Summary.Id.ToString()
                            && item.Status == SyncStatus.Conflict);
                });
            }
            finally
            {
                await worker.StopAsync(default);
                worker.Dispose();
            }
        }

        await using (var recoveryContext = database.CreateContext())
        {
            var recovery = CreateSessionService(recoveryContext, cloud, signal, options);
            var conflicted = await recoveryContext.SyncQueueSet
                .AsNoTracking()
                .SingleAsync(item => item.EntityType == "exam_sessions"
                    && item.EntityId == created.Summary.Id.ToString());
            using var conflictedPayload = JsonDocument.Parse(conflicted.PayloadJson);
            var conflictedUpdatedAt = conflictedPayload.RootElement
                .GetProperty("updated_at")
                .GetDateTimeOffset();
            var changed = await recovery.ChangePublicCloudRoomCodeAsync(
                created.Summary.Id,
                new("NEW133", created.Summary.RowVersion),
                default);
            Assert.Equal(created.Summary.Id, changed.Summary.Id);
            Assert.Equal("NEW133", changed.Summary.RoomCode);
            Assert.Equal(
                1,
                await recoveryContext.ExamSessionsSet.CountAsync());
            var rearmed = Assert.Single(await recoveryContext.SyncQueueSet
                .Where(item => item.EntityType == "exam_sessions"
                    && item.EntityId == created.Summary.Id.ToString())
                .ToListAsync());
            Assert.Equal(SyncStatus.Pending, rearmed.Status);
            Assert.Equal(0, rearmed.RetryCount);
            Assert.Contains("\"room_code\":\"NEW133\"", rearmed.PayloadJson, StringComparison.Ordinal);
            using var rearmedPayload = JsonDocument.Parse(rearmed.PayloadJson);
            var rearmedUpdatedAt = rearmedPayload.RootElement
                .GetProperty("updated_at")
                .GetDateTimeOffset();
            var refreshedSession = await recoveryContext.ExamSessionsSet
                .AsNoTracking()
                .SingleAsync(session => session.Id == created.Summary.Id);
            Assert.Equal(refreshedSession.UpdatedAtUtc, rearmedUpdatedAt);
            Assert.True(rearmedUpdatedAt > conflictedUpdatedAt);
        }

        await using (var provider = services.BuildServiceProvider())
        {
            var worker = new CloudSyncWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                options,
                signal,
                NullLogger<CloudSyncWorker>.Instance);
            await worker.StartAsync(default);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    await using var verify = database.CreateContext();
                    return await verify.SyncQueueSet.AnyAsync(
                        item => item.EntityType == "exam_sessions"
                            && item.EntityId == created.Summary.Id.ToString()
                            && item.Status == SyncStatus.Synced);
                });
            }
            finally
            {
                await worker.StopAsync(default);
                worker.Dispose();
            }
        }

        await using var readinessContext = database.CreateContext();
        var readinessService = CreateSessionService(readinessContext, cloud, signal, options);
        var ready = await readinessService.GetProjectionReadinessAsync(
            created.Summary.Id,
            default);
        Assert.True(ready.Ready);
        Assert.Equal("PUBLICCLOUD_PROJECTION_READY", ready.Code);
        Assert.Equal(2, cloud.SessionPushCount);
    }

    [Fact]
    public async Task PublicRoomCodeRecovery_RejectsLanStaleActivityAndLocalDuplicate()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var cloud = new SuccessfulPushCloud();
        var signal = new RecordingCloudSyncSignal();
        var options = Options.Create(new ExamTransferOptions());

        var lan = Session(exam, "LANSAFE", SessionAccessMode.LanOnly);
        var active = Session(exam, "TAKEN133", SessionAccessMode.PublicCloud);
        var withActivity = Session(exam, "ACT133", SessionAccessMode.PublicCloud);
        var recoverable = Session(exam, "OLD133", SessionAccessMode.PublicCloud);
        withActivity.Participants.Add(new SessionParticipant
        {
            Session = withActivity,
            StudentCode = "ACTIVITY",
            DisplayName = "Activity Student",
            DeviceId = "activity-device",
            MachineName = "activity-machine",
            AppVersion = "test"
        });
        database.Context.ExamSessionsSet.AddRange(lan, active, withActivity, recoverable);
        foreach (var session in new[] { lan, withActivity, recoverable })
        {
            var item = ProjectionItem(
                session.Id,
                SyncStatus.Conflict,
                DateTimeOffset.UtcNow,
                retryCount: 1);
            item.LastError = RoomConflictFailure();
            database.Context.SyncQueueSet.Add(item);
        }
        await database.Context.SaveChangesAsync();
        var service = CreateSessionService(database.Context, cloud, signal, options);

        var lanError = await Assert.ThrowsAsync<ApiException>(() =>
            service.ChangePublicCloudRoomCodeAsync(
                lan.Id,
                new("NEWLAN", lan.RowVersion),
                default));
        Assert.Equal(ErrorCodes.InvalidStateTransition, lanError.Code);

        var stale = await Assert.ThrowsAsync<ApiException>(() =>
            service.ChangePublicCloudRoomCodeAsync(
                recoverable.Id,
                new("NEWSTALE", "stale-row-version"),
                default));
        Assert.Equal(ErrorCodes.ConcurrencyConflict, stale.Code);

        var activity = await Assert.ThrowsAsync<ApiException>(() =>
            service.ChangePublicCloudRoomCodeAsync(
                withActivity.Id,
                new("NEWACT", withActivity.RowVersion),
                default));
        Assert.Equal(ErrorCodes.InvalidStateTransition, activity.Code);

        var duplicate = await Assert.ThrowsAsync<ApiException>(() =>
            service.ChangePublicCloudRoomCodeAsync(
                recoverable.Id,
                new("TAKEN133", recoverable.RowVersion),
                default));
        Assert.Equal(ErrorCodes.RoomCodeConflict, duplicate.Code);
        Assert.Equal(4, await database.Context.ExamSessionsSet.CountAsync());
        Assert.Equal("OLD133", recoverable.RoomCode);
        Assert.Equal(0, signal.PulseCount);
    }

    [Fact]
    public async Task ProjectionReadiness_IsReadOnlyAndUsesNewestExamSessionQueueRow()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var lanSession = Session(exam, "READLAN", SessionAccessMode.LanOnly);
        var cloudSession = Session(exam, "READCLOUD", SessionAccessMode.PublicCloud);
        database.Context.ExamSessionsSet.AddRange(lanSession, cloudSession);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var signal = new RecordingCloudSyncSignal();
        var execution = new PublicCloudProjectionExecution(database.Context, signal);
        var rowCountBefore = await database.Context.SyncQueueSet.CountAsync();

        var lan = await execution.GetProjectionReadinessAsync(
            lanSession.Id,
            default);
        var missing = await execution.GetProjectionReadinessAsync(
            cloudSession.Id,
            default);

        Assert.False(lan.Required);
        Assert.True(lan.Ready);
        Assert.Equal(SyncStatus.LocalOnly, lan.Status);
        Assert.Equal("LAN_ONLY", lan.Code);
        Assert.True(missing.Required);
        Assert.False(missing.Ready);
        Assert.Equal(SyncStatus.Pending, missing.Status);
        Assert.Equal("PUBLICCLOUD_PROJECTION_PENDING", missing.Code);
        Assert.Equal(rowCountBefore, await database.Context.SyncQueueSet.CountAsync());
        Assert.Equal(0, signal.PulseCount);
        Assert.DoesNotContain(
            database.Context.ChangeTracker.Entries(),
            entry => entry.State is EntityState.Added
                or EntityState.Modified
                or EntityState.Deleted);

        var older = ProjectionItem(
            cloudSession.Id,
            SyncStatus.Synced,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            retryCount: 1);
        var newest = ProjectionItem(
            cloudSession.Id,
            SyncStatus.Conflict,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            retryCount: 7);
        database.Context.SyncQueueSet.AddRange(older, newest);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var countWithProjection = await database.Context.SyncQueueSet.CountAsync();

        var conflicted = await execution.GetProjectionReadinessAsync(
            cloudSession.Id,
            default);

        Assert.True(conflicted.Required);
        Assert.False(conflicted.Ready);
        Assert.Equal(SyncStatus.Conflict, conflicted.Status);
        Assert.Equal("PUBLICCLOUD_PROJECTION_FAILED", conflicted.Code);
        Assert.Equal(7, conflicted.RetryCount);
        Assert.Equal(
            countWithProjection,
            await database.Context.SyncQueueSet.CountAsync());
        Assert.Equal(0, signal.PulseCount);
        Assert.DoesNotContain(
            typeof(PublicCloudProjectionExecution)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType),
            type => type == typeof(ICloudAdapter));
    }

    [Fact]
    public async Task RetryProjection_UpdatesNewestQueueInPlaceWithoutResettingRetryMetadata()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var session = Session(exam, "RETRYCLOUD", SessionAccessMode.PublicCloud);
        var lastAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
        var payload = """{"id":"projection-payload","source_mode":"Lan"}""";
        var item = ProjectionItem(
            session.Id,
            SyncStatus.Failed,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            retryCount: 4);
        item.PayloadJson = payload;
        item.LastError = "transport failed";
        item.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        item.NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(10);
        item.LastAttemptAtUtc = lastAttemptAtUtc;
        item.CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        database.Context.ExamSessionsSet.Add(session);
        database.Context.SyncQueueSet.Add(item);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var signal = new RecordingCloudSyncSignal();
        var execution = new PublicCloudProjectionExecution(database.Context, signal);
        var beforeRetry = DateTimeOffset.UtcNow;
        var retrying = await execution.RetryProjectionAsync(session.Id, default);
        var afterRetry = DateTimeOffset.UtcNow;

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.SyncQueueSet.SingleAsync(
            row => row.Id == item.Id);
        Assert.False(retrying.Ready);
        Assert.Equal(SyncStatus.Pending, retrying.Status);
        Assert.Equal("PUBLICCLOUD_PROJECTION_SYNCING", retrying.Code);
        Assert.Equal(SyncStatus.Pending, persisted.Status);
        Assert.Equal(4, persisted.RetryCount);
        Assert.Equal(payload, persisted.PayloadJson);
        Assert.Equal("upsert", persisted.Operation);
        Assert.Equal(lastAttemptAtUtc, persisted.LastAttemptAtUtc);
        Assert.Null(persisted.LastError);
        Assert.Null(persisted.LeaseUntilUtc);
        Assert.Null(persisted.CompletedAtUtc);
        Assert.NotNull(persisted.NextRetryAtUtc);
        Assert.InRange(
            persisted.NextRetryAtUtc.Value,
            beforeRetry,
            afterRetry);
        Assert.Equal(1, signal.PulseCount);
        Assert.Equal(
            1,
            await database.Context.SyncQueueSet.CountAsync(
                row => row.EntityType == "exam_sessions"
                    && row.EntityId == session.Id.ToString()));

        var beforeRepeatedRetry = DateTimeOffset.UtcNow;
        var repeated = await execution.RetryProjectionAsync(session.Id, default);

        database.Context.ChangeTracker.Clear();
        var repeatedItem = await database.Context.SyncQueueSet.SingleAsync(
            row => row.Id == item.Id);
        Assert.Equal(SyncStatus.Pending, repeated.Status);
        Assert.Equal(4, repeatedItem.RetryCount);
        Assert.True(repeatedItem.NextRetryAtUtc >= beforeRepeatedRetry);
        Assert.Equal(2, signal.PulseCount);
        Assert.Equal(
            1,
            await database.Context.SyncQueueSet.CountAsync(
                row => row.EntityType == "exam_sessions"
                    && row.EntityId == session.Id.ToString()));
    }

    [Fact]
    public async Task RetryProjection_LanSyncedAndMissingContractsRemainUnchanged()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var lanSession = Session(exam, "RETRYLAN", SessionAccessMode.LanOnly);
        var syncedSession = Session(exam, "RETRYSYNC", SessionAccessMode.PublicCloud);
        var missingSession = Session(exam, "RETRYMISS", SessionAccessMode.PublicCloud);
        var lanItem = ProjectionItem(
            lanSession.Id,
            SyncStatus.Failed,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            retryCount: 3);
        lanItem.LastError = "lan queue must remain unchanged";
        var syncedItem = ProjectionItem(
            syncedSession.Id,
            SyncStatus.Synced,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            retryCount: 5);
        syncedItem.CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        database.Context.ExamSessionsSet.AddRange(
            lanSession,
            syncedSession,
            missingSession);
        database.Context.SyncQueueSet.AddRange(lanItem, syncedItem);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var signal = new RecordingCloudSyncSignal();
        var execution = new PublicCloudProjectionExecution(database.Context, signal);

        var lan = await execution.RetryProjectionAsync(lanSession.Id, default);
        var synced = await execution.RetryProjectionAsync(
            syncedSession.Id,
            default);
        var missing = await Assert.ThrowsAsync<ApiException>(() =>
            execution.RetryProjectionAsync(missingSession.Id, default));

        database.Context.ChangeTracker.Clear();
        var persistedLan = await database.Context.SyncQueueSet
            .AsNoTracking()
            .SingleAsync(row => row.Id == lanItem.Id);
        var persistedSynced = await database.Context.SyncQueueSet
            .AsNoTracking()
            .SingleAsync(row => row.Id == syncedItem.Id);
        Assert.False(lan.Required);
        Assert.True(lan.Ready);
        Assert.Equal(SyncStatus.LocalOnly, lan.Status);
        Assert.True(synced.Required);
        Assert.True(synced.Ready);
        Assert.Equal(SyncStatus.Synced, synced.Status);
        Assert.Equal(SyncStatus.Failed, persistedLan.Status);
        Assert.Equal("lan queue must remain unchanged", persistedLan.LastError);
        Assert.Equal(3, persistedLan.RetryCount);
        Assert.Equal(SyncStatus.Synced, persistedSynced.Status);
        Assert.Equal(5, persistedSynced.RetryCount);
        Assert.Equal(ErrorCodes.Conflict, missing.Code);
        Assert.Equal(409, missing.StatusCode);
        Assert.Equal(0, signal.PulseCount);
        Assert.Equal(2, await database.Context.SyncQueueSet.CountAsync());
    }

    [Fact]
    public async Task RuntimeHealth_RunsSchemaPreflightAndReportsBothWorkersHealthy()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var cloud = new SuccessfulPushCloud();
        var options = Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true
            }
        });
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={database.Path}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        await using var provider = services.BuildServiceProvider();
        var reporter = new RuntimeHealthReporter(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestStoragePaths(Path.GetDirectoryName(database.Path)!),
            options,
            new DiscoveryRuntimeState());

        var report = await reporter.GetAsync(CancellationToken.None);

        Assert.Equal("Healthy", report.SupabaseSchemaCompatible.Status);
        Assert.Equal(
            "SUPABASE_SCHEMA_COMPATIBLE",
            report.SupabaseSchemaCompatible.Code);
        Assert.Equal("Healthy", report.CloudWorker.Status);
        Assert.Equal("CLOUD_WORKER_HEALTHY", report.CloudWorker.Code);
        Assert.Equal("Healthy", report.PublicCloudPullWorker.Status);
        Assert.Equal(
            "PUBLIC_CLOUD_PULL_HEALTHY",
            report.PublicCloudPullWorker.Code);
        Assert.Equal(1, cloud.HealthChecks);
    }

    [Fact]
    public async Task RuntimeHealth_FailedSchemaPreflightKeepsBothWorkersBlocked()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var cloud = new SuccessfulPushCloud { HealthResult = false };
        var options = Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true
            }
        });
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={database.Path}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        await using var provider = services.BuildServiceProvider();
        var reporter = new RuntimeHealthReporter(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestStoragePaths(Path.GetDirectoryName(database.Path)!),
            options,
            new DiscoveryRuntimeState());

        var report = await reporter.GetAsync(CancellationToken.None);

        Assert.Equal("Degraded", report.SupabaseSchemaCompatible.Status);
        Assert.Equal(
            "SUPABASE_SCHEMA_INCOMPATIBLE_OR_UNREACHABLE",
            report.SupabaseSchemaCompatible.Code);
        Assert.Equal("Degraded", report.CloudWorker.Status);
        Assert.Equal(
            "CLOUD_WORKER_BLOCKED_BY_PREFLIGHT",
            report.CloudWorker.Code);
        Assert.Equal("Degraded", report.PublicCloudPullWorker.Status);
        Assert.Equal(
            "PUBLIC_CLOUD_PULL_BLOCKED_BY_PREFLIGHT",
            report.PublicCloudPullWorker.Code);
        Assert.Equal(1, cloud.HealthChecks);
    }

    private static SessionService CreateSessionService(
        AppDbContext db,
        ICloudAdapter cloud,
        ICloudSyncSignal signal,
        IOptions<ExamTransferOptions> options)
    {
        var audit = new AuditService(db, new HttpContextAccessor());
        var outbox = new OutboxService(db, signal);
        var realtime = new NoOpRealtime();
        var tokens = new SessionTokenService(options);
        var participantMutations = new SessionParticipantMutationDispatcher(
            db,
            new ISessionParticipantMutationHandler[]
            {
                new LanSessionParticipantMutationHandler(
                    db,
                    tokens,
                    audit,
                    outbox,
                    realtime,
                    options),
                new PublicCloudSessionParticipantMutationHandler(
                    options,
                    realtime,
                    cloud)
            });
        return new SessionService(
            db,
            audit,
            outbox,
            realtime,
            options,
            NullLogger<SessionService>.Instance,
            participantMutations,
            new LanParticipantSessionExecution(
                db,
                tokens,
                audit,
                outbox,
                realtime,
                options,
                new LanAccessPolicy(options)),
            new PublicCloudProjectionExecution(db, signal),
            signal);
    }

    private static async Task<Exam> SeedPublishedExamAsync(AppDbContext db)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            Title = "Projection readiness",
            Subject = "Test",
            DurationMinutes = 45,
            Status = ExamStatus.Published,
            DeliveryType = ExamDeliveryType.FileSubmission
        };
        db.ExamsSet.Add(exam);
        await db.SaveChangesAsync();
        return exam;
    }

    private static ExamSession Session(
        Exam exam,
        string roomCode,
        SessionAccessMode accessMode) =>
        new()
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = roomCode,
            Status = SessionStatus.Waiting,
            AcceptingParticipants = true,
            AccessMode = accessMode,
            AdmissionMode = SessionAdmissionMode.OpenRequest,
            DeliveryTypeSnapshot = exam.DeliveryType,
            SupervisionModeSnapshot = exam.SupervisionMode,
            QuizResultPolicySnapshot = exam.QuizResultPolicy,
            ExamVersionSnapshot = exam.Version
        };

    private static SyncQueueItem ProjectionItem(
        Guid sessionId,
        SyncStatus status,
        DateTimeOffset createdAtUtc,
        int retryCount) =>
        new()
        {
            EntityType = "exam_sessions",
            EntityId = sessionId.ToString(),
            Operation = "upsert",
            PayloadJson = """{"id":"projection"}""",
            Status = status,
            RetryCount = retryCount,
            CreatedAtUtc = createdAtUtc
        };

    private static CreateSessionRequest Request(Guid examId) => new(
        examId,
        null,
        DateTimeOffset.UtcNow.AddMinutes(5),
        "{}",
        false,
        36,
        "PUB133",
        SessionAccessMode.PublicCloud,
        SessionAdmissionMode.OpenRequest);

    private static string RoomConflictFailure() =>
        JsonSerializer.Serialize(
            new CloudSyncFailure(
                ErrorCodes.RoomCodeConflict,
                "Mã phòng PublicCloud đang hoạt động đã được sử dụng trong tổ chức."),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++)
            await Task.Delay(10);
        Assert.True(await condition());
    }

    private sealed class NoOpRealtime : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingCloudSyncSignal : ICloudSyncSignal
    {
        public int PulseCount { get; private set; }

        public void Pulse() => PulseCount++;

        public Task<bool> WaitAsync(
            TimeSpan maximumDelay,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private class SuccessfulPushCloud : ICloudAdapter
    {
        public int HealthChecks { get; private set; }
        public bool HealthResult { get; init; } = true;
        public bool Enabled => true;
        public bool Configured => true;
        public bool Authenticated => true;
        public bool CanSynchronize => true;
        public CloudLoginResult? CurrentSession => null;
        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
        {
            HealthChecks++;
            return Task.FromResult(HealthResult);
        }
        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public virtual Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new CloudPushResult(false, null, "test", 0));
        }
        public Task<CloudLoginResult> LoginAsync(
            string email,
            string password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CloudLoginResult?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);
        public Task DownloadObjectAsync(
            string cloudObjectPath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RoomConflictPushCloud : SuccessfulPushCloud
    {
        public int SessionPushCount { get; private set; }

        public override Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken)
        {
            if (string.Equals(item.EntityType, "exam_sessions", StringComparison.Ordinal))
            {
                SessionPushCount++;
                throw new ApiException(
                    ErrorCodes.RoomCodeConflict,
                    "Mã phòng PublicCloud đang hoạt động đã được sử dụng trong tổ chức.",
                    409);
            }

            return base.PushAsync(item, checkpoint, cancellationToken);
        }
    }

    private sealed class RecoverableRoomConflictPushCloud : SuccessfulPushCloud
    {
        public int SessionPushCount { get; private set; }

        public override Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken)
        {
            if (string.Equals(item.EntityType, "exam_sessions", StringComparison.Ordinal))
            {
                SessionPushCount++;
                if (item.PayloadJson.Contains("\"room_code\":\"PUB133\"", StringComparison.Ordinal))
                    throw new ApiException(
                        ErrorCodes.RoomCodeConflict,
                        "Mã phòng PublicCloud đang hoạt động đã được sử dụng trong tổ chức.",
                        409);
            }
            return base.PushAsync(item, checkpoint, cancellationToken);
        }
    }

    private sealed class TestStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) =>
            Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) =>
            Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) =>
            Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) =>
            Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}
