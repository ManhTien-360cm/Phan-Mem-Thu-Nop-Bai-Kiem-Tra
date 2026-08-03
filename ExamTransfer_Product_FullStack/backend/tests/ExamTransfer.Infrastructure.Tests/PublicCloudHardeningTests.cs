using System.Text.Json;
using System.Reflection;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Cloud;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Execution.OnlyLan;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class FinalCloudSourceCompatibilityTests
{
    [Fact]
    public async Task ActivePublicRoomUniqueViolation_MapsToTypedRoomCodeConflict()
    {
        var ensureSuccess = typeof(SupabaseCloudAdapter).GetMethod(
            "EnsureSuccessAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ensureSuccess);
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """
                {"code":"23505","message":"duplicate key value violates unique constraint \"ux_exam_sessions_active_public_room\""}
                """)
        };

        var invocation = Assert.IsAssignableFrom<Task>(ensureSuccess!.Invoke(
            null,
            [response, "Supabase metadata insert", CancellationToken.None]));
        var error = await Assert.ThrowsAsync<ApiException>(() => invocation);

        Assert.Equal(ErrorCodes.RoomCodeConflict, error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Contains("Mã phòng PublicCloud", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardMigration_AddsOnlySourceCloudVersionAndCapability18()
    {
        var sql = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260726064745_final_remaining_quiz_source_cloud_version.sql");

        Assert.Contains(
            "alter table public.quiz_import_sources",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "add column if not exists cloud_version bigint not null default 0",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "set schema_version = 18",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "create or replace function public.get_examtransfer_cloud_capabilities",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop policy", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create policy", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuizSourcePayloadAndObjectPath_AreStableLocalOwnedAndRequireSchema22()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExamTransfer.CloudSource.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var organizationId = Guid.NewGuid();
            var options = Options.Create(new ExamTransferOptions
            {
                Cloud = new CloudOptions
                {
                    OrganizationId = organizationId.ToString(),
                    Environment = "Tests",
                    ExamBucket = "exam-archives"
                }
            });
            var paths = new CloudSourcePaths(root);
            var sessionState = new CloudSessionState(
                DataProtectionProvider.Create(root),
                paths);
            var adapter = new SupabaseCloudAdapter(
                new HttpClient(),
                options,
                sessionState);
            var sourceId = Guid.NewGuid();

            var buildPayload = typeof(SupabaseCloudAdapter).GetMethod(
                "BuildPayload",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(buildPayload);
            var payloadJson = Assert.IsType<string>(buildPayload!.Invoke(
                adapter,
                [
                    "quiz_import_sources",
                    JsonSerializer.Serialize(new
                    {
                        id = sourceId,
                        original_name = "source.docx",
                        mime_type = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        updated_at = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero)
                    }),
                    null
                ]));
            using var payload = JsonDocument.Parse(payloadJson);
            Assert.Equal(sourceId, payload.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(organizationId, payload.RootElement.GetProperty("organization_id").GetGuid());
            Assert.True(payload.RootElement.GetProperty("cloud_version").GetInt64() > 0);

            var resolveTarget = typeof(SupabaseCloudAdapter).GetMethod(
                "ResolveStorageTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(resolveTarget);
            string Resolve(string fileName)
            {
                var target = resolveTarget!.Invoke(
                    adapter,
                    ["quiz_import_sources", sourceId.ToString(), fileName]);
                Assert.NotNull(target);
                return Assert.IsType<string>(target!.GetType()
                    .GetProperty("ObjectPath")!
                    .GetValue(target));
            }

            var firstPath = Resolve("first-random.docx");
            var replacementPath = Resolve("second-random.pdf");
            Assert.Equal(firstPath, replacementPath);
            Assert.EndsWith(
                $"/quiz-sources/{sourceId}/source.bin",
                firstPath,
                StringComparison.Ordinal);
            Assert.Equal(25, CloudSchemaCompatibility.RequiredVersion);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublicCloudCapability_RequiresSchema23AndCriticalRpcs()
    {
        Assert.Equal(25, CloudSchemaCompatibility.RequiredVersion);
        Assert.Contains("save_public_quiz_grade", CloudSchemaCompatibility.CriticalRpcs);
        Assert.Contains("return_public_quiz_grade", CloudSchemaCompatibility.CriticalRpcs);
        Assert.Contains("reopen_public_quiz_grade", CloudSchemaCompatibility.CriticalRpcs);
        Assert.Contains("report_public_violation", CloudSchemaCompatibility.CriticalRpcs);
        Assert.Contains("ack_public_device_command", CloudSchemaCompatibility.CriticalRpcs);

        var script = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/scripts/test-cloud-schema-version.ps1");
        Assert.Contains("schemaVersion -ne 25", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'save_public_quiz_grade'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'return_public_quiz_grade'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'reopen_public_quiz_grade'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'get_public_quiz_attempt_review'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'report_public_violation'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'ack_public_device_command'", script, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CloudSourcePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temp");
        public string ExamVersionRoot(Guid examId, int version) =>
            Path.Combine(RootPath, "exams", examId.ToString("N"), version.ToString());
        public string SessionRoot(Guid sessionId) =>
            Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) =>
            Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) =>
            Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}

public sealed class PublicCloudTeacherMutationTests
{
    [Fact]
    public void Projection_roundtrip_sends_non_empty_teacher_mutation_request_id()
    {
        var script = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/scripts/test-public-cloud-projection-roundtrip.ps1");

        Assert.Contains(
            "$mutationRequestId = [Guid]::NewGuid()",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "mutationRequestId = $mutationRequestId",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Body $approveBody",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Body '{}'",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Teacher_mutation_acceptance_sends_a_non_empty_request_id_for_every_call()
    {
        var script = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/scripts/test-public-cloud-teacher-mutations.ps1");
        var calls = new[]
        {
            (
                Variable: "approveMutationRequestId",
                Path: "api/v1/sessions/$SessionId/participants/$ApproveParticipantId/approve"),
            (
                Variable: "rejectMutationRequestId",
                Path: "api/v1/sessions/$SessionId/participants/$RejectParticipantId/reject"),
            (
                Variable: "extraTimeMutationRequestId",
                Path: "api/v1/sessions/$SessionId/participants/$ExtraTimeParticipantId/extra-time"),
            (
                Variable: "resubmitMutationRequestId",
                Path: "api/v1/participants/$ResubmitParticipantId/allow-resubmit"),
            (
                Variable: "submissionRejectMutationRequestId",
                Path: "api/v1/submissions/$SubmissionId/reject")
        };

        foreach (var call in calls)
        {
            Assert.Contains(
                $"${call.Variable} = [Guid]::NewGuid()",
                script,
                StringComparison.Ordinal);
            var invocationIndex = script.IndexOf(
                $"Invoke-LocalMutation \"{call.Path}\" @{{",
                StringComparison.Ordinal);
            Assert.True(invocationIndex >= 0, $"Missing mutation call for {call.Path}.");
            var bodyEndIndex = script.IndexOf(
                "} | Out-Null",
                invocationIndex,
                StringComparison.Ordinal);
            Assert.True(bodyEndIndex > invocationIndex, $"Missing request body end for {call.Path}.");
            var requestIdIndex = script.IndexOf(
                $"mutationRequestId = ${call.Variable}",
                invocationIndex,
                StringComparison.Ordinal);
            Assert.True(
                requestIdIndex > invocationIndex && requestIdIndex < bodyEndIndex,
                $"Mutation request ID is outside the request body for {call.Path}.");
        }

        Assert.Equal(
            calls.Length,
            script.Split("MutationRequestId = [Guid]::NewGuid()", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            calls.Length,
            script.Split("mutationRequestId =", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Migration_exposes_narrow_security_definer_rpcs_with_authenticated_only_grants()
    {
        var sql = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260723043859_public_cloud_teacher_mutations_and_projection.sql");
        foreach (var rpc in new[]
        {
            "approve_public_participant", "reject_public_participant",
            "bulk_approve_public_participants", "add_public_participant_extra_time",
            "allow_public_resubmission", "reject_public_submission",
            "approve_public_enrollment_request", "reject_public_enrollment_request"
        })
        {
            Assert.Contains($"function public.{rpc}", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"grant execute on function public.{rpc}", sql, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("security definer", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set search_path = ''", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private.begin_public_teacher_mutation", sql, StringComparison.Ordinal);
        Assert.Contains("private.write_public_teacher_audit", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Et01_forward_migration_keeps_narrow_grants_and_atomic_absolute_deadline_contract()
    {
        var sql = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260725164934_public_cloud_time_realtime_completion.sql");

        Assert.Contains("status = 'InProgress'", sql, StringComparison.Ordinal);
        Assert.Contains("set deadline_at = v_deadline", sql, StringComparison.Ordinal);
        Assert.Contains("'serverNowUtc', v_server_now", sql, StringComparison.Ordinal);
        Assert.Contains("'effectiveDeadlineUtc', v_deadline", sql, StringComparison.Ordinal);
        Assert.Contains("perform realtime.send", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private.finish_public_teacher_mutation", sql, StringComparison.Ordinal);
        Assert.Contains("get_public_student_timeline", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("grant update", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Supabase_adapter_maps_et01_rpc_time_attempt_and_revision_fields()
    {
        var participantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            participantId,
            sessionId,
            status = "Approved",
            approvedAt = now,
            extraTimeMinutes = 15,
            resubmitAllowed = false,
            resubmitReason = (string?)null,
            cloudVersion = 50,
            updatedAt = now,
            effectiveDeadline = now.AddHours(1),
            attemptId,
            attemptStatus = "InProgress",
            attemptDeadline = now.AddHours(1),
            attemptRevision = 51,
            serverNowUtc = now,
            revision = 51,
            requestId
        }));
        var parser = typeof(ExamTransfer.Infrastructure.Cloud.SupabaseCloudAdapter)
            .GetMethod("ParseParticipantMutation", BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<CloudParticipantMutationResult>(
            parser!.Invoke(null, [document.RootElement]));

        Assert.Equal(attemptId, result.AttemptId);
        Assert.Equal(now.AddHours(1), result.AttemptDeadlineUtc);
        Assert.Equal(now, result.ServerNowUtc);
        Assert.Equal(51, result.Revision);
        Assert.Equal(requestId, result.RequestId);
    }
}

public sealed class PublicCloudTeacherMutationRoutingTests
{
    [Fact]
    public async Task Approve_PublicCloud_calls_rpc_without_mutating_sqlite_or_creating_outbox()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);
        var mutationRequestId = Guid.NewGuid();

        var result = await service.ApproveAsync(participant.SessionId, participant.Id, mutationRequestId, CancellationToken.None);

        Assert.Equal(1, cloud.ApproveCalls);
        Assert.Equal(mutationRequestId, cloud.LastApproveRequestId);
        Assert.Equal(ParticipantStatus.Approved, result.Status);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task Failed_PublicCloud_rpc_does_not_fake_approval_in_sqlite()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter { FailApprove = true };
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);
        var mutationRequestId = Guid.NewGuid();

        await Assert.ThrowsAsync<ApiException>(
            () => service.ApproveAsync(participant.SessionId, participant.Id, mutationRequestId, CancellationToken.None));
        Assert.Equal(mutationRequestId, cloud.LastApproveRequestId);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task LanOnly_approval_keeps_existing_sqlite_and_outbox_flow()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.LanOnly);
        var cloud = new RecordingCloudAdapter { FailApprove = true };
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);

        await service.ApproveAsync(participant.SessionId, participant.Id, Guid.Empty, CancellationToken.None);

        Assert.Equal(0, cloud.ApproveCalls);
        Assert.Equal(
            ParticipantStatus.Approved,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).Status);
        Assert.Contains(
            await database.Context.SyncQueueSet.ToListAsync(),
            x => x.EntityType == "session_participants" && x.EntityId == participant.Id.ToString());
    }

    [Fact]
    public async Task Reject_PublicCloud_calls_rpc_without_local_mutation_or_outbox()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);
        var requestId = Guid.NewGuid();

        await service.RejectAsync(
            participant.SessionId,
            participant.Id,
            "Identity mismatch",
            requestId,
            CancellationToken.None);

        Assert.Equal(1, cloud.RejectParticipantCalls);
        Assert.Equal(requestId, cloud.LastRejectParticipantRequestId);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (await database.Context.SessionParticipantsSet
                .SingleAsync(x => x.Id == participant.Id))
                .Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task BulkApprove_PublicCloud_calls_rpc_without_local_mutation_or_outbox()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);
        var requestId = Guid.NewGuid();

        var result = await service.BulkApproveAsync(
            participant.SessionId,
            new BulkApproveRequest([participant.Id, participant.Id], requestId),
            CancellationToken.None);

        Assert.Equal(1, cloud.BulkApproveCalls);
        Assert.Equal(requestId, cloud.LastBulkApproveRequestId);
        Assert.Equal([participant.Id], cloud.LastBulkApproveParticipantIds);
        Assert.Equal(ParticipantStatus.Approved, Assert.Single(result).Status);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (await database.Context.SessionParticipantsSet
                .SingleAsync(x => x.Id == participant.Id))
                .Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task PublicCloud_participant_mutations_require_request_id_before_rpc()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);

        var rejectError = await Assert.ThrowsAsync<ApiException>(() =>
            service.RejectAsync(
                participant.SessionId,
                participant.Id,
                null,
                Guid.Empty,
                CancellationToken.None));
        var bulkError = await Assert.ThrowsAsync<ApiException>(() =>
            service.BulkApproveAsync(
                participant.SessionId,
                new BulkApproveRequest([participant.Id], Guid.Empty),
                CancellationToken.None));
        var extraTimeError = await Assert.ThrowsAsync<ApiException>(() =>
            service.AddExtraTimeAsync(
                participant.SessionId,
                participant.Id,
                new ExtraTimeRequest(10, "Accommodation", Guid.Empty),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, rejectError.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, bulkError.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, extraTimeError.Code);
        Assert.Equal(0, cloud.RejectParticipantCalls);
        Assert.Equal(0, cloud.BulkApproveCalls);
        Assert.Equal(0, cloud.ExtraTimeCalls);
    }

    [Fact]
    public async Task PublicCloud_rpc_failures_do_not_fallback_to_Lan_or_write_local_state()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter
        {
            FailRejectParticipant = true,
            FailBulkApprove = true,
            FailExtraTime = true
        };
        var realtime = new RecordingRealtimePublisher();
        var service = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            cloud,
            realtime);

        await Assert.ThrowsAsync<ApiException>(() => service.RejectAsync(
            participant.SessionId,
            participant.Id,
            "Rejected upstream",
            Guid.NewGuid(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => service.BulkApproveAsync(
            participant.SessionId,
            new BulkApproveRequest([participant.Id], Guid.NewGuid()),
            CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => service.AddExtraTimeAsync(
            participant.SessionId,
            participant.Id,
            new ExtraTimeRequest(10, "Upstream failure", Guid.NewGuid()),
            CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.SessionParticipantsSet
            .SingleAsync(x => x.Id == participant.Id);
        Assert.Equal(ParticipantStatus.PendingApproval, persisted.Status);
        Assert.Equal(0, persisted.ExtraTimeMinutes);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
        Assert.Empty(realtime.SessionEvents);
    }

    [Fact]
    public async Task PublicCloud_extra_time_maps_absolute_contract_and_broadcasts_once_without_local_mutation()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var realtime = new RecordingRealtimePublisher();
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud, realtime);
        var requestId = Guid.NewGuid();

        var result = await service.AddExtraTimeAsync(
            participant.SessionId,
            participant.Id,
            new ExtraTimeRequest(15, "Approved accommodation", requestId),
            CancellationToken.None);

        Assert.Equal(1, cloud.ExtraTimeCalls);
        Assert.Equal(requestId, cloud.LastExtraTimeRequestId);
        Assert.Equal(15, result.ExtraTimeMinutes);
        Assert.Equal(cloud.ExtraTimeResult!.EffectiveDeadlineUtc, result.EffectiveDeadlineUtc);
        var published = Assert.Single(realtime.SessionEvents);
        Assert.Equal(participant.SessionId, published.SessionId);
        Assert.Equal(RealtimeEvents.TimeExtended, published.EventName);
        Assert.Equal(cloud.ExtraTimeResult.Revision, published.Sequence);
        var payload = Assert.IsType<TimeExtendedEvent>(published.Payload);
        Assert.Equal(participant.Id, payload.ParticipantId);
        Assert.Equal(cloud.ExtraTimeResult.AttemptId, payload.AttemptId);
        Assert.Equal(cloud.ExtraTimeResult.EffectiveDeadlineUtc, payload.EffectiveDeadlineUtc);
        Assert.Equal(cloud.ExtraTimeResult.ServerNowUtc, payload.ServerNowUtc);
        Assert.Equal(requestId, payload.RequestId);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            0,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id))
                .ExtraTimeMinutes);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task PublicCloud_extra_time_failure_or_incomplete_time_contract_does_not_broadcast()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var realtime = new RecordingRealtimePublisher();
        var cloud = new RecordingCloudAdapter { ExtraTimeMissingContract = true };
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud, realtime);

        var error = await Assert.ThrowsAsync<ApiException>(() => service.AddExtraTimeAsync(
            participant.SessionId,
            participant.Id,
            new ExtraTimeRequest(10, "Incomplete upstream response", Guid.NewGuid()),
            CancellationToken.None));

        Assert.Equal(ErrorCodes.CloudUploadFailed, error.Code);
        Assert.Empty(realtime.SessionEvents);
    }

    [Fact]
    public async Task PublicCloud_resubmit_and_submission_reject_use_rpcs_without_local_final_state()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        participant.SubmissionStatus = SubmissionStatus.Submitted;
        var submission = new Submission
        {
            Id = Guid.NewGuid(), Participant = participant, SessionId = participant.SessionId,
            AttemptNumber = 1, IdempotencyKey = "public-routing-test",
            Status = SubmissionStatus.Submitted, ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1), SourceMode = "PublicCloud"
        };
        database.Context.SubmissionsSet.Add(submission);
        await database.Context.SaveChangesAsync();
        var cloud = new RecordingCloudAdapter();
        var service = CreateSubmissionService(database.Context, cloud);

        var resubmitRequestId = Guid.NewGuid();
        var rejectRequestId = Guid.NewGuid();
        await service.AllowResubmitAsync(participant.Id, new("Approved retry", resubmitRequestId), CancellationToken.None);
        await service.RejectAsync(submission.Id, new("Unreadable archive", rejectRequestId), CancellationToken.None);

        Assert.Equal(1, cloud.ResubmitCalls);
        Assert.Equal(1, cloud.RejectSubmissionCalls);
        Assert.Equal(resubmitRequestId, cloud.LastResubmitRequestId);
        Assert.Equal(rejectRequestId, cloud.LastRejectSubmissionRequestId);
        Assert.Equal("Approved retry", cloud.LastResubmitReason);
        Assert.Equal("Unreadable archive", cloud.LastRejectSubmissionReason);
        database.Context.ChangeTracker.Clear();
        Assert.False((await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).ResubmitAllowed);
        Assert.Equal(
            SubmissionStatus.Submitted,
            (await database.Context.SubmissionsSet.SingleAsync(x => x.Id == submission.Id)).Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task PublicCloud_submission_mutations_require_request_id_before_rpc()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var submission = new Submission
        {
            Participant = participant,
            SessionId = participant.SessionId,
            AttemptNumber = 1,
            IdempotencyKey = "public-request-id-test",
            Status = SubmissionStatus.Submitted,
            ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1),
            SourceMode = "PublicCloud"
        };
        database.Context.SubmissionsSet.Add(submission);
        await database.Context.SaveChangesAsync();
        var cloud = new RecordingCloudAdapter();
        var service = CreateSubmissionService(database.Context, cloud);

        var resubmitError = await Assert.ThrowsAsync<ApiException>(() =>
            service.AllowResubmitAsync(
                participant.Id,
                new AllowResubmitRequest("Retry", Guid.Empty),
                CancellationToken.None));
        var rejectError = await Assert.ThrowsAsync<ApiException>(() =>
            service.RejectAsync(
                submission.Id,
                new RejectSubmissionRequest("Reject", Guid.Empty),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, resubmitError.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, rejectError.Code);
        Assert.Equal(0, cloud.ResubmitCalls);
        Assert.Equal(0, cloud.RejectSubmissionCalls);
    }

    [Fact]
    public async Task PublicCloud_submission_rpc_failures_do_not_fallback_or_write_local_state()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        participant.SubmissionStatus = SubmissionStatus.Submitted;
        var submission = new Submission
        {
            Participant = participant,
            SessionId = participant.SessionId,
            AttemptNumber = 1,
            IdempotencyKey = "public-failure-test",
            Status = SubmissionStatus.Submitted,
            ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1),
            SourceMode = "PublicCloud"
        };
        database.Context.SubmissionsSet.Add(submission);
        await database.Context.SaveChangesAsync();
        var cloud = new RecordingCloudAdapter
        {
            FailResubmit = true,
            FailRejectSubmission = true
        };
        var service = CreateSubmissionService(database.Context, cloud);

        await Assert.ThrowsAsync<ApiException>(() =>
            service.AllowResubmitAsync(
                participant.Id,
                new AllowResubmitRequest("Retry failure", Guid.NewGuid()),
                CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() =>
            service.RejectAsync(
                submission.Id,
                new RejectSubmissionRequest("Reject failure", Guid.NewGuid()),
                CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        Assert.False(
            (await database.Context.SessionParticipantsSet
                .SingleAsync(x => x.Id == participant.Id))
                .ResubmitAllowed);
        Assert.Equal(
            SubmissionStatus.Submitted,
            (await database.Context.SubmissionsSet
                .SingleAsync(x => x.Id == submission.Id))
                .Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task PublicCloudRealtime_teacher_message_uses_rpc_without_local_write_or_signalr_fallback()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var realtime = new RecordingRealtimePublisher();
        var service = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            cloud,
            realtime);

        var result = await service.SendMessageAsync(
            participant.SessionId,
            new SendMessageRequest(participant.Id, MessageType.Warning, "PublicCloud message"),
            CancellationToken.None);

        Assert.Equal(1, cloud.SendMessageCalls);
        Assert.NotEqual(Guid.Empty, cloud.LastSendMessageRequestId);
        Assert.Equal(participant.Id, cloud.LastMessageParticipantId);
        Assert.Equal("PublicCloud message", result.Content);
        Assert.Empty(await database.Context.MessagesSet.ToListAsync());
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
        Assert.Equal(0, realtime.PublishCount);
    }

    [Fact]
    public async Task PublicCloudRealtime_teacher_message_rpc_failure_does_not_fallback_or_write_local_state()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter { FailSendMessage = true };
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);

        await Assert.ThrowsAsync<ApiException>(() => service.SendMessageAsync(
            participant.SessionId,
            new SendMessageRequest(null, MessageType.Information, "No fallback"),
            CancellationToken.None));

        Assert.Equal(1, cloud.SendMessageCalls);
        Assert.Empty(await database.Context.MessagesSet.ToListAsync());
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public void PublicCloudRealtime_migration_keeps_typed_transactional_and_rls_contracts()
    {
        var migration = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260802145519_complete_publiccloud_student_realtime.sql");

        foreach (var eventType in Enum.GetNames<StudentNotificationEventType>())
            Assert.Contains($"'{eventType}'", migration, StringComparison.Ordinal);
        Assert.Contains("enable row level security", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("participant.user_id = (select auth.uid())", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session.access_mode = 'PublicCloud'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mutation_request_id", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supabase_realtime", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("using (true)", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_role key", migration, StringComparison.OrdinalIgnoreCase);
    }

    private static SubmissionService CreateSubmissionService(
        AppDbContext db,
        ICloudAdapter cloud)
    {
        var options = Options.Create(new ExamTransferOptions());
        var audit = new AuditService(db, new HttpContextAccessor());
        var outbox = new OutboxService(db);
        var realtime = new TestRealtimePublisher();
        var dispatcher = new SubmissionMutationDispatcher(
            db,
            new ISubmissionMutationHandler[]
            {
                new LanSubmissionMutationHandler(
                    db,
                    audit,
                    outbox),
                new PublicCloudSubmissionMutationHandler(cloud)
            });
        return new SubmissionService(
            db,
            new TestStoragePaths(),
            new ChunkStorage(),
            new ReceiptSigner(options),
            audit,
            outbox,
            realtime,
            options,
            dispatcher);
    }

    private sealed class TestRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public List<PublishedEvent> SessionEvents { get; } = [];
        public int PublishCount { get; private set; }

        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            PublishCount++;
            SessionEvents.Add(new(sessionId, eventName, sequence, payload!));
            return Task.CompletedTask;
        }

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            PublishCount++;
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedEvent(Guid SessionId, string EventName, long Sequence, object Payload);

    private sealed class TestStoragePaths : IStoragePaths
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "ExamTransfer.PublicCloud.Storage");
        public string DatabasePath => Path.Combine(RootPath, "database.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temp");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), version.ToString());
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}

public sealed class PublicCloudPullProjectionTests
{
    [Fact]
    public async Task Pull_projects_enrollment_and_approved_participant_into_business_tables()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var classroom = new ClassRoom
        {
            Id = Guid.NewGuid(), Name = "Public class", Code = "PUB", SchoolYear = "2026-2027",
            AccessMode = ClassAccessMode.Public
        };
        database.Context.ClassesSet.Add(classroom);
        await database.Context.SaveChangesAsync();
        var enrollmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["class_enrollment_requests"] = new(
                "class_enrollment_requests", enrollmentId.ToString(), 10, now,
                JsonSerializer.Serialize(new
                {
                    id = enrollmentId, class_id = classroom.Id, student_user_id = Guid.NewGuid(),
                    student_code = "SV-PUB", status = "Pending", requested_at = now,
                    decided_at = (DateTimeOffset?)null, decided_by = (Guid?)null,
                    decision_reason = (string?)null, updated_at = now, cloud_version = 10
                })),
            ["session_participants"] = new(
                "session_participants", participant.Id.ToString(), 11, now.AddSeconds(1),
                JsonSerializer.Serialize(new
                {
                    id = participant.Id, session_id = participant.SessionId, user_id = Guid.NewGuid(),
                    student_code = participant.StudentCode, display_name = participant.DisplayName,
                    device_id = participant.DeviceId, machine_name = participant.MachineName,
                    app_version = participant.AppVersion, status = "Approved", joined_at = now,
                    approved_at = now, last_seen_at = now, download_status = "NotStarted",
                    submission_status = "NotStarted", extra_time_minutes = 0,
                    resubmit_allowed = false, updated_at = now, cloud_version = 11
                }))
        });

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);
        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);

        await using var verify = database.CreateContext();
        var classService = new ClassService(
            verify,
            new MemoryCache(new MemoryCacheOptions()),
            new AuditService(verify, new HttpContextAccessor()),
            new OutboxService(verify));
        var classDetail = await classService.GetAsync(classroom.Id, CancellationToken.None);
        Assert.Equal("Pending", Assert.Single(classDetail.EnrollmentRequests!).Status);
        var projected = await verify.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id);
        Assert.Equal(ParticipantStatus.Approved, projected.Status);
        Assert.Equal("PublicCloud", projected.SourceMode);
        Assert.Equal(11, projected.CloudVersion);
        Assert.Single(await verify.PublicCloudReplicaRecordsSet.Where(
            x => x.EntityName == "session_participants" && x.CloudEntityId == participant.Id.ToString()).ToListAsync());
        Assert.Equal(
            11,
            (await verify.PublicCloudPullCursorsSet.SingleAsync(x => x.EntityName == "session_participants")).LastCloudVersion);
    }

    [Fact]
    public async Task Pull_connects_member_device_command_and_quiz_projections_to_services()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var userId = Guid.NewGuid();
        var classroom = new ClassRoom
        {
            Id = Guid.NewGuid(), Name = "Projection class", Code = "PROJ", SchoolYear = "2026-2027",
            AccessMode = ClassAccessMode.Public
        };
        var existingMember = new ClassMember
        {
            Id = Guid.NewGuid(), Class = classroom, UserId = userId,
            StudentCode = "SV-PROJ", DisplayName = "Existing member"
        };
        var question = new QuizQuestion
        {
            Id = Guid.NewGuid(), ExamId = participant.Session.ExamId, Version = 1,
            Order = 1, Text = "Projected question", Points = 1
        };
        database.Context.ClassMembersSet.Add(existingMember);
        database.Context.QuizQuestionsSet.Add(question);
        await database.Context.SaveChangesAsync();

        var cloudMemberId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var answerId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var records = new Dictionary<string, CloudPullRecord>
        {
            ["class_members"] = new("class_members", cloudMemberId.ToString(), 20, now,
                JsonSerializer.Serialize(new
                {
                    id = cloudMemberId, class_id = classroom.Id, user_id = userId,
                    student_code = "SV-PROJ", display_name = "Cloud member",
                    email = "student@example.test", metadata_json = new { source = "enrollment" }
                })),
            ["public_device_connections"] = new("public_device_connections", connectionId.ToString(), 21, now.AddSeconds(1),
                JsonSerializer.Serialize(new
                {
                    id = connectionId, session_id = participant.SessionId, participant_id = participant.Id,
                    user_id = userId, device_id = "device-projected", connection_state = "Online",
                    heartbeat_at = now, policy_state = "Applied", lock_state = "Locked",
                    violation_count = 2, app_version = "2.0", agent_version = "3.0"
                })),
            ["public_device_commands"] = new("public_device_commands", commandId.ToString(), 22, now.AddSeconds(2),
                JsonSerializer.Serialize(new
                {
                    command_id = commandId, session_id = participant.SessionId, device_id = "device-projected",
                    command_type = "LockExamApplication", payload = new { }, created_at = now,
                    expires_at = now.AddMinutes(5), issued_by = Guid.NewGuid(),
                    signature = new string('a', 64), retry_count = 0
                })),
            ["public_device_command_results"] = new("public_device_command_results", commandId.ToString(), 23, now.AddSeconds(3),
                JsonSerializer.Serialize(new
                {
                    command_id = commandId, device_id = "device-projected", status = "Failed",
                    received_at = now, executed_at = now, error_code = "AGENT_ERROR",
                    error_message = "Command failed in acceptance fixture"
                })),
            ["quiz_attempts"] = new("quiz_attempts", attemptId.ToString(), 24, now.AddSeconds(4),
                JsonSerializer.Serialize(new
                {
                    id = attemptId, session_id = participant.SessionId, participant_id = participant.Id,
                    exam_version = 1, status = "InProgress", started_at = now,
                    deadline_at = now.AddHours(1), finalized_at = (DateTimeOffset?)null,
                    score = (decimal?)null, max_score = 1,
                    snapshot_json = new[]
                    {
                        new
                        {
                            id = question.Id, sortOrder = 1, questionText = "Projected question",
                            points = 1, multiple = false,
                            choices = new[] { new { id = choiceId, sortOrder = 1, choiceText = "Visible choice" } }
                        }
                    },
                    finalize_idempotency_key = (string?)null
                })),
            ["quiz_answers"] = new("quiz_answers", answerId.ToString(), 25, now.AddSeconds(5),
                JsonSerializer.Serialize(new
                {
                    id = answerId, attempt_id = attemptId, question_id = question.Id,
                    choice_ids = new[] { choiceId }, revision = 3, client_updated_at = now
                }))
        };

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, new PullCloudAdapter(records));

        await using var verify = database.CreateContext();
        Assert.Single(await verify.ClassMembersSet.Where(x => x.ClassId == classroom.Id && x.UserId == userId).ToListAsync());
        Assert.Equal(
            existingMember.Id,
            (await verify.PublicCloudIdMappingsSet.SingleAsync(
                x => x.EntityName == "class_members" && x.CloudEntityId == cloudMemberId.ToString())).LocalEntityId);

        var control = new ControlService(
            verify,
            new AuditService(verify, new HttpContextAccessor()),
            new TestRealtimePublisher(),
            new OutboxService(verify),
            new DeviceStatusReadExecution(verify));
        var device = Assert.Single(await control.GetDeviceStatusAsync(participant.SessionId, CancellationToken.None));
        Assert.Equal(ConnectionState.Online, device.ConnectionState);
        Assert.Equal("Locked", device.LockState);
        Assert.Equal("3.0", device.AgentVersion);
        Assert.Equal(DeviceCommandStatus.Failed, device.LastCommandStatus);
        Assert.Equal("Command failed in acceptance fixture", device.LastCommandError);

        var attempts = await new QuizService(
                verify,
                new QuizProjectionOutbox(new OutboxService(verify)))
            .ListAttemptsForSessionAsync(participant.SessionId, CancellationToken.None);
        var attempt = Assert.Single(attempts);
        Assert.Equal(attemptId, attempt.Id);
        Assert.Equal(now.AddHours(1), attempt.DeadlineUtc);
        Assert.Equal(3, Assert.Single(attempt.Answers).Revision);
        var projectedQuestion = Assert.Single(attempt.Questions);
        Assert.Equal("Projected question", projectedQuestion.Text);
        Assert.Equal("Visible choice", Assert.Single(projectedQuestion.Choices).Text);

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, new PullCloudAdapter(
            new Dictionary<string, CloudPullRecord>
            {
                ["quiz_attempts"] = new("quiz_attempts", attemptId.ToString(), 23, now.AddMinutes(2),
                    JsonSerializer.Serialize(new
                    {
                        id = attemptId, session_id = participant.SessionId, participant_id = participant.Id,
                        exam_version = 1, status = "InProgress", started_at = now,
                        deadline_at = now.AddHours(3), score = (decimal?)null, max_score = 1,
                        snapshot_json = Array.Empty<object>()
                    }))
            }));
        verify.ChangeTracker.Clear();
        Assert.Equal(
            now.AddHours(1),
            (await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId)).DeadlineUtc);

        var finalized = await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId);
        finalized.Status = QuizAttemptStatus.Finalized;
        finalized.FinalizedAtUtc = now;
        finalized.Score = 1;
        await verify.SaveChangesAsync();
        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, new PullCloudAdapter(
            new Dictionary<string, CloudPullRecord>
            {
                ["quiz_attempts"] = new("quiz_attempts", attemptId.ToString(), 30, now.AddMinutes(1),
                    JsonSerializer.Serialize(new
                    {
                        id = attemptId, session_id = participant.SessionId, participant_id = participant.Id,
                        exam_version = 1, status = "InProgress", started_at = now,
                        deadline_at = now.AddHours(2), score = (decimal?)null, max_score = 1,
                        snapshot_json = Array.Empty<object>()
                    }))
            }));
        verify.ChangeTracker.Clear();
        var protectedAttempt = await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId);
        Assert.Equal(QuizAttemptStatus.Finalized, protectedAttempt.Status);
        Assert.Equal(1, protectedAttempt.Score);
    }

    private sealed class TestRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class PublicCloudOutboxLoopPreventionTests
{
    [Fact]
    public async Task Pulled_projection_does_not_create_reverse_sync_queue_item()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var now = DateTimeOffset.UtcNow;
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["session_participants"] = new(
                "session_participants", participant.Id.ToString(), 7, now,
                JsonSerializer.Serialize(new
                {
                    id = participant.Id, session_id = participant.SessionId,
                    student_code = participant.StudentCode, display_name = participant.DisplayName,
                    device_id = participant.DeviceId, machine_name = participant.MachineName,
                    app_version = participant.AppVersion, status = "Approved", joined_at = now,
                    download_status = "NotStarted", submission_status = "NotStarted",
                    extra_time_minutes = 0, resubmit_allowed = false,
                    updated_at = now, cloud_version = 7
                }))
        });

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);

        await using var verify = database.CreateContext();
        Assert.Empty(await verify.SyncQueueSet.ToListAsync());
    }
}

public sealed class PublicCloudCursorTransactionTests
{
    [Fact]
    public async Task Projection_failure_rolls_back_replica_and_does_not_advance_cursor()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var badId = Guid.NewGuid();
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["class_members"] = new(
                "class_members", badId.ToString(), 99, DateTimeOffset.UtcNow,
                """{"student_code":"missing-required-class-id","display_name":"Broken"}""")
        });

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);

        await using var verify = database.CreateContext();
        Assert.False(await verify.PublicCloudReplicaRecordsSet.AnyAsync(
            x => x.EntityName == "class_members" && x.CloudEntityId == badId.ToString()));
        var cursor = await verify.PublicCloudPullCursorsSet
            .SingleOrDefaultAsync(x => x.EntityName == "class_members");
        Assert.True(cursor is null || cursor.LastCloudVersion == 0);
        Assert.Contains(
            await verify.PublicCloudPullFailuresSet.ToListAsync(),
            x => x.EntityName == "class_members" && x.ResolvedAtUtc == null);
    }
}

public sealed class PublicCloudMigrationSafetyTests
{
    [Fact]
    public void Compatibility_migration_uses_partial_indexes_and_preflight_guards_optional_columns()
    {
        var migration = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260723043859_public_cloud_teacher_mutations_and_projection.sql");
        var preflight = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/preflight/public_cloud_production_legacy_preflight.sql");

        Assert.Contains("where source_mode = 'PublicCloud'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drop index if exists public.ux_submission_files_submission", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if new.source_mode <> 'PublicCloud' then return new", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("information_schema.columns", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execute $sql$", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BLOCKER|", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("delete from", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update public.", preflight, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PublicCloudTestHarness
{
    public static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExamTransfer.PublicCloud.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "public-cloud.db");
        var context = CreateContext(path);
        await context.Database.EnsureCreatedAsync();
        return new TestDatabase(directory, path, context);
    }

    public static async Task<SessionParticipant> SeedSessionAsync(AppDbContext db, SessionAccessMode accessMode)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(), Title = "PublicCloud", Subject = "Test", DurationMinutes = 60,
            Status = ExamStatus.Published
        };
        var session = new ExamSession
        {
            Id = Guid.NewGuid(), Exam = exam, RoomCode = Guid.NewGuid().ToString("N")[..8],
            Status = SessionStatus.Waiting, HostDeviceId = "host", AccessMode = accessMode
        };
        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(), Session = session, StudentCode = "SV001", DisplayName = "Student",
            DeviceId = "device-1", MachineName = "machine", AppVersion = "1.0",
            Status = ParticipantStatus.PendingApproval,
            SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan"
        };
        db.SessionParticipantsSet.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    public static SessionService CreateSessionService(
        AppDbContext db,
        ICloudAdapter cloud,
        IRealtimePublisher? realtime = null)
    {
        var options = Options.Create(new ExamTransferOptions());
        var tokens = new SessionTokenService(options);
        var audit = new AuditService(db, new HttpContextAccessor());
        var outbox = new OutboxService(db);
        var publisher = realtime ?? new NoOpRealtimePublisher();
        var participantMutations = new SessionParticipantMutationDispatcher(
            db,
            new ISessionParticipantMutationHandler[]
            {
                new LanSessionParticipantMutationHandler(
                    db,
                    tokens,
                    audit,
                    outbox,
                    publisher,
                    options),
                new PublicCloudSessionParticipantMutationHandler(
                    options,
                    publisher,
                    cloud)
            });
        return new SessionService(
            db,
            audit,
            outbox,
            publisher,
            options,
            NullLogger<SessionService>.Instance,
            participantMutations,
            new LanParticipantSessionExecution(
                db,
                tokens,
                audit,
                outbox,
                publisher,
                options,
                new LanAccessPolicy(options)),
            new PublicCloudProjectionExecution(db),
            cloudAdapter: cloud);
    }

    public static async Task RunPullOnceAsync(string databasePath, ICloudAdapter cloud)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(cloud);
        await using var provider = services.BuildServiceProvider();
        var worker = new PublicCloudPullWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicCloudPullWorker>.Instance);
        await worker.PullOnceAsync(CancellationToken.None);
    }

    public static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "ExamTransfer.slnx"))
               && !File.Exists(Path.Combine(directory.FullName, "ExamTransfer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(directory!.FullName, "ExamTransfer.sln"))
            && normalized.StartsWith($"backend{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            normalized = normalized[("backend".Length + 1)..];
        return File.ReadAllText(Path.Combine(directory.FullName, normalized));
    }

    private static AppDbContext CreateContext(string path) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);

    internal sealed class TestDatabase(string directory, string path, AppDbContext context) : IAsyncDisposable
    {
        public string Path { get; } = path;
        public AppDbContext Context { get; } = context;
        public AppDbContext CreateContext() => PublicCloudTestHarness.CreateContext(Path);
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

internal class RecordingCloudAdapter : ICloudAdapter
{
    public int ApproveCalls { get; private set; }
    public int RejectParticipantCalls { get; private set; }
    public int BulkApproveCalls { get; private set; }
    public int ResubmitCalls { get; private set; }
    public int RejectSubmissionCalls { get; private set; }
    public int ExtraTimeCalls { get; private set; }
    public int SendMessageCalls { get; private set; }
    public Guid? LastApproveRequestId { get; private set; }
    public Guid? LastRejectParticipantRequestId { get; private set; }
    public Guid? LastBulkApproveRequestId { get; private set; }
    public IReadOnlyList<Guid>? LastBulkApproveParticipantIds { get; private set; }
    public Guid? LastResubmitRequestId { get; private set; }
    public Guid? LastRejectSubmissionRequestId { get; private set; }
    public string? LastResubmitReason { get; private set; }
    public string? LastRejectSubmissionReason { get; private set; }
    public Guid? LastExtraTimeRequestId { get; private set; }
    public Guid? LastSendMessageRequestId { get; private set; }
    public Guid? LastMessageParticipantId { get; private set; }
    public bool FailApprove { get; init; }
    public bool FailRejectParticipant { get; init; }
    public bool FailBulkApprove { get; init; }
    public bool FailExtraTime { get; init; }
    public bool FailResubmit { get; init; }
    public bool FailRejectSubmission { get; init; }
    public bool ExtraTimeMissingContract { get; init; }
    public bool FailSendMessage { get; init; }
    public CloudParticipantMutationResult? ExtraTimeResult { get; private set; }
    public bool Enabled => true;
    public bool Configured => true;
    public bool Authenticated => true;
    public bool CanSynchronize => true;
    public CloudLoginResult? CurrentSession => null;
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CloudPushResult> PushAsync(SyncQueueItem item, Func<CancellationToken, Task>? checkpoint, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<CloudPullPage> PullAsync(
        string entityName, CloudPullCursorValue cursor, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new CloudPullPage([], false));
    public Task<CloudParticipantMutationResult> ApprovePublicParticipantAsync(Guid sessionId, Guid participantId, Guid requestId, CancellationToken cancellationToken)
    {
        ApproveCalls++;
        LastApproveRequestId = requestId;
        if (FailApprove) throw new ApiException(ErrorCodes.CloudUploadFailed, "Simulated RPC failure", 502);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CloudParticipantMutationResult(
            participantId, sessionId, ParticipantStatus.Approved, now, 0, false, null, 42, now));
    }
    public Task<CloudParticipantMutationResult> RejectPublicParticipantAsync(
        Guid sessionId,
        Guid participantId,
        string? reason,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        RejectParticipantCalls++;
        LastRejectParticipantRequestId = requestId;
        if (FailRejectParticipant)
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Simulated reject RPC failure",
                502);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CloudParticipantMutationResult(
            participantId,
            sessionId,
            ParticipantStatus.Rejected,
            null,
            0,
            false,
            reason,
            43,
            now));
    }
    public Task<CloudBulkParticipantMutationResult> BulkApprovePublicParticipantsAsync(
        Guid sessionId,
        IReadOnlyList<Guid> participantIds,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        BulkApproveCalls++;
        LastBulkApproveRequestId = requestId;
        LastBulkApproveParticipantIds = participantIds.ToList();
        if (FailBulkApprove)
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Simulated bulk approve RPC failure",
                502);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CloudBulkParticipantMutationResult(
            participantIds.Count,
            0,
            participantIds
                .Select(id => new CloudParticipantMutationResult(
                    id,
                    sessionId,
                    ParticipantStatus.Approved,
                    now,
                    0,
                    false,
                    null,
                    44,
                    now))
                .ToList()));
    }
    public Task<CloudParticipantMutationResult> AllowPublicResubmissionAsync(
        Guid participantId, string reason, Guid requestId, CancellationToken cancellationToken)
    {
        ResubmitCalls++;
        LastResubmitRequestId = requestId;
        LastResubmitReason = reason;
        if (FailResubmit)
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Simulated resubmit RPC failure",
                502);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CloudParticipantMutationResult(
            participantId, Guid.Empty, ParticipantStatus.Approved, now, 0, true, reason, 43, now));
    }
    public Task<CloudParticipantMutationResult> AddPublicParticipantExtraTimeAsync(
        Guid sessionId,
        Guid participantId,
        int minutes,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        ExtraTimeCalls++;
        LastExtraTimeRequestId = requestId;
        if (FailExtraTime)
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Simulated extra-time RPC failure",
                502);
        var now = DateTimeOffset.UtcNow;
        ExtraTimeResult = new(
            participantId,
            sessionId,
            ParticipantStatus.Approved,
            now,
            minutes,
            false,
            null,
            50,
            now,
            now.AddMinutes(75),
            Guid.NewGuid(),
            "InProgress",
            now.AddMinutes(75),
            51,
            ExtraTimeMissingContract ? null : now,
            ExtraTimeMissingContract ? null : 51,
            requestId);
        return Task.FromResult(ExtraTimeResult);
    }
    public Task<CloudSubmissionMutationResult> RejectPublicSubmissionAsync(
        Guid submissionId, string reason, Guid requestId, CancellationToken cancellationToken)
    {
        RejectSubmissionCalls++;
        LastRejectSubmissionRequestId = requestId;
        LastRejectSubmissionReason = reason;
        if (FailRejectSubmission)
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Simulated submission reject RPC failure",
                502);
        return Task.FromResult(new CloudSubmissionMutationResult(
            submissionId, Guid.Empty, Guid.Empty, SubmissionStatus.Rejected, reason, 44, DateTimeOffset.UtcNow));
    }
    public Task<MessageDto> SendPublicTeacherMessageAsync(
        Guid sessionId,
        Guid? participantId,
        MessageType messageType,
        string content,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        SendMessageCalls++;
        LastSendMessageRequestId = requestId;
        LastMessageParticipantId = participantId;
        if (FailSendMessage)
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Simulated teacher message RPC failure",
                502);
        return Task.FromResult(new MessageDto(
            Guid.NewGuid(),
            sessionId,
            Guid.NewGuid(),
            participantId,
            messageType,
            content,
            DateTimeOffset.UtcNow));
    }
    public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => Task.FromResult<CloudLoginResult?>(null);
    public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);
    public Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class PullCloudAdapter(IReadOnlyDictionary<string, CloudPullRecord> records) : RecordingCloudAdapter
{
    public override string ToString() => nameof(PullCloudAdapter);

    public override Task<CloudPullPage> PullAsync(
        string entityName, CloudPullCursorValue cursor, int limit, CancellationToken cancellationToken)
    {
        if (records.TryGetValue(entityName, out var record) && cursor.CloudVersion < record.CloudVersion)
            return Task.FromResult(new CloudPullPage([record], false));
        return Task.FromResult(new CloudPullPage([], false));
    }
}
