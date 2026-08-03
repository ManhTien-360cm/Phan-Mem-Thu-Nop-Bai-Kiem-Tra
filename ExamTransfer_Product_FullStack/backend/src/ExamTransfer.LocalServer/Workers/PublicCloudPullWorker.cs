using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Workers;

public sealed class PublicCloudPullWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PublicCloudPullWorker> logger,
    IRealtimePublisher? realtime = null) : BackgroundService, IPublicCloudPullWorker
{
    private static readonly string[] EntityOrder =
    [
        "class_enrollment_requests", "class_members", "session_participants",
        "public_device_connections", "violations", "public_device_commands",
        "public_device_command_results", "submissions", "submission_files",
        "quiz_attempts", "quiz_answers"
    ];
    private static readonly int[] RetrySeconds = [5, 15, 30, 60, 120, 300];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PublicCloud pull cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    public async Task PullOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cloud = scope.ServiceProvider.GetRequiredService<ICloudAdapter>();
        if (!cloud.CanSynchronize)
            return;
        if (!await cloud.CheckHealthAsync(cancellationToken))
        {
            await RecordFailureAsync(db, "cloud_schema", null, "schema",
                $"Cloud schema/capabilities do not match required version {CloudSchemaCompatibility.RequiredVersion}.",
                null, cancellationToken);
            return;
        }

        foreach (var entityName in EntityOrder)
        {
            var unresolvedRetryTimes = await db.PublicCloudPullFailuresSet
                .Where(x => x.EntityName == entityName && x.ResolvedAtUtc == null)
                .Select(x => x.NextRetryAtUtc)
                .ToListAsync(cancellationToken);
            var blockedUntil = unresolvedRetryTimes
                .OrderByDescending(x => x)
                .FirstOrDefault();
            if (blockedUntil > DateTimeOffset.UtcNow)
                continue;

            try
            {
                await PullEntityAsync(db, cloud, entityName, cancellationToken);
                var failures = await db.PublicCloudPullFailuresSet
                    .Where(x => x.EntityName == entityName && x.ResolvedAtUtc == null)
                    .ToListAsync(cancellationToken);
                foreach (var failure in failures)
                    failure.ResolvedAtUtc = DateTimeOffset.UtcNow;
                if (failures.Count > 0)
                    await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecordFailureAsync(db, entityName, null, Classify(ex), ex.Message, null, cancellationToken);
                logger.LogWarning(ex, "PublicCloud pull failed for {EntityName}", entityName);
            }
        }
    }

    private async Task PullEntityAsync(
        AppDbContext db,
        ICloudAdapter cloud,
        string entityName,
        CancellationToken cancellationToken)
    {
        var committedProjectionVersions = new Dictionary<Guid, long>();
        var cursor = await db.PublicCloudPullCursorsSet
            .SingleOrDefaultAsync(x => x.EntityName == entityName, cancellationToken);
        cursor ??= new PublicCloudPullCursor { EntityName = entityName };
        if (db.Entry(cursor).State == EntityState.Detached)
            db.PublicCloudPullCursorsSet.Add(cursor);

        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            var page = await cloud.PullAsync(
                entityName,
                new CloudPullCursorValue(cursor.LastCloudVersion, cursor.LastUpdatedAtUtc, cursor.LastEntityId),
                100,
                cancellationToken);
            if (page.Records.Count == 0)
            {
                await PublishProjectionUpdatesAsync(committedProjectionVersions, cancellationToken);
                return;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var pageProjectionVersions = new Dictionary<Guid, long>();
            try
            {
                foreach (var record in page.Records)
                {
                    var existing = await db.PublicCloudReplicaRecordsSet.SingleOrDefaultAsync(
                        x => x.EntityName == record.EntityName && x.CloudEntityId == record.EntityId,
                        cancellationToken);
                    if (existing is null)
                    {
                        db.PublicCloudReplicaRecordsSet.Add(new PublicCloudReplicaRecord
                        {
                            EntityName = record.EntityName,
                            CloudEntityId = record.EntityId,
                            CloudVersion = record.CloudVersion,
                            CloudUpdatedAtUtc = record.UpdatedAtUtc,
                            PayloadJson = record.PayloadJson
                        });
                    }
                    else if (record.CloudVersion > existing.CloudVersion)
                    {
                        existing.CloudVersion = record.CloudVersion;
                        existing.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                        existing.PayloadJson = record.PayloadJson;
                    }
                    else if (record.CloudVersion == existing.CloudVersion
                             && !JsonEquivalent(existing.PayloadJson, record.PayloadJson))
                    {
                        throw new DbUpdateConcurrencyException(
                            $"The same cloud_version produced different payloads for {record.EntityName}/{record.EntityId}.");
                    }

                    long? previousParticipantVersion = null;
                    if (record.EntityName == "session_participants"
                        && Guid.TryParse(record.EntityId, out var participantId))
                    {
                        previousParticipantVersion = (await db.SessionParticipantsSet
                            .FindAsync([participantId], cancellationToken))?.CloudVersion;
                    }

                    var localId = await ApplyTeacherProjectionAsync(db, record, cancellationToken);
                    if (localId.HasValue)
                    {
                        var mapping = await db.PublicCloudIdMappingsSet.SingleOrDefaultAsync(
                            x => x.EntityName == record.EntityName && x.CloudEntityId == record.EntityId,
                            cancellationToken);
                        if (mapping is null)
                        {
                            db.PublicCloudIdMappingsSet.Add(new PublicCloudIdMapping
                            {
                                EntityName = record.EntityName,
                                CloudEntityId = record.EntityId,
                                LocalEntityId = localId.Value
                            });
                        }
                        else
                        {
                            mapping.LocalEntityId = localId.Value;
                        }
                    }

                    if (record.EntityName == "session_participants"
                        && localId.HasValue
                        && (!previousParticipantVersion.HasValue
                            || record.CloudVersion > previousParticipantVersion.Value))
                    {
                        var participant = await db.SessionParticipantsSet
                            .FindAsync([localId.Value], cancellationToken);
                        if (participant is not null)
                        {
                            var accessMode = await db.ExamSessionsSet
                                .Where(x => x.Id == participant.SessionId)
                                .Select(x => (SessionAccessMode?)x.AccessMode)
                                .SingleOrDefaultAsync(cancellationToken);
                            if (accessMode == SessionAccessMode.PublicCloud)
                            {
                                pageProjectionVersions[participant.SessionId] =
                                    Math.Max(
                                        pageProjectionVersions.GetValueOrDefault(participant.SessionId),
                                        record.CloudVersion);
                            }
                        }
                    }
                }

                var last = page.Records[^1];
                cursor.LastCloudVersion = last.CloudVersion;
                cursor.LastUpdatedAtUtc = last.UpdatedAtUtc;
                cursor.LastEntityId = last.EntityId;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                foreach (var update in pageProjectionVersions)
                {
                    committedProjectionVersions[update.Key] = Math.Max(
                        committedProjectionVersions.GetValueOrDefault(update.Key),
                        update.Value);
                }
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
            if (!page.HasMore)
            {
                await PublishProjectionUpdatesAsync(committedProjectionVersions, cancellationToken);
                return;
            }
        }

        await PublishProjectionUpdatesAsync(committedProjectionVersions, cancellationToken);
    }

    private async Task PublishProjectionUpdatesAsync(
        IReadOnlyDictionary<Guid, long> projectionVersions,
        CancellationToken cancellationToken)
    {
        if (realtime is null)
            return;

        foreach (var update in projectionVersions.OrderBy(x => x.Key))
        {
            try
            {
                var payload = new PublicCloudProjectionUpdatedEvent(
                    update.Key,
                    PublicCloudProjectionEntityTypes.SessionParticipant,
                    update.Value);
                await realtime.PublishSessionAsync(
                    update.Key,
                    RealtimeEvents.PublicCloudProjectionUpdated,
                    update.Value,
                    payload,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "PublicCloud projection publish failed after local commit. SessionId={SessionId}; EntityType={EntityType}; ProjectionVersion={ProjectionVersion}",
                    update.Key,
                    PublicCloudProjectionEntityTypes.SessionParticipant,
                    update.Value);
            }
        }
    }

    private static async Task<Guid?> ApplyTeacherProjectionAsync(
        AppDbContext db,
        CloudPullRecord record,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(record.EntityId, out var id)) return null;
        using var document = JsonDocument.Parse(record.PayloadJson);
        var row = document.RootElement;
        switch (record.EntityName)
        {
            case "class_enrollment_requests":
            {
                var entity = await db.ClassEnrollmentRequestsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new ClassEnrollmentRequest { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.ClassEnrollmentRequestsSet.Add(entity);
                entity.ClassId = GuidValue(row, "class_id");
                entity.StudentUserId = GuidValue(row, "student_user_id");
                entity.StudentCode = StringValue(row, "student_code");
                entity.Status = StringValue(row, "status");
                entity.RequestedAtUtc = DateValue(row, "requested_at", record.UpdatedAtUtc);
                entity.DecidedAtUtc = NullableDate(row, "decided_at");
                entity.DecidedBy = NullableGuid(row, "decided_by");
                entity.DecisionReason = NullableString(row, "decision_reason");
                Stamp(entity, record);
                return entity.Id;
            }
            case "class_members":
            {
                var userId = NullableGuid(row, "user_id");
                var classId = GuidValue(row, "class_id");
                var studentCode = StringValue(row, "student_code");
                var entity = await db.ClassMembersSet.FindAsync([id], cancellationToken);
                entity ??= userId.HasValue
                    ? await db.ClassMembersSet.FirstOrDefaultAsync(
                        x => x.ClassId == classId && x.UserId == userId,
                        cancellationToken)
                    : null;
                entity ??= await db.ClassMembersSet.FirstOrDefaultAsync(
                    x => x.ClassId == classId && x.StudentCode == studentCode,
                    cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new ClassMember { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.ClassMembersSet.Add(entity);
                entity.ClassId = classId;
                entity.UserId = userId;
                entity.StudentCode = studentCode;
                entity.DisplayName = StringValue(row, "display_name");
                entity.Email = NullableString(row, "email");
                entity.MetadataJson = RawOrNull(row, "metadata_json");
                Stamp(entity, record);
                return entity.Id;
            }
            case "session_participants":
            {
                var entity = await db.SessionParticipantsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new SessionParticipant { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.SessionParticipantsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.UserId = NullableGuid(row, "user_id");
                entity.StudentCode = StringValue(row, "student_code");
                entity.DisplayName = StringValue(row, "display_name");
                entity.ClassName = NullableString(row, "class_name");
                entity.DeviceId = NullableString(row, "device_id") ?? string.Empty;
                entity.MachineName = NullableString(row, "machine_name") ?? string.Empty;
                entity.IpAddress = NullableString(row, "ip_address");
                entity.AppVersion = NullableString(row, "app_version") ?? string.Empty;
                entity.Status = EnumValue<ParticipantStatus>(row, "status");
                entity.JoinedAtUtc = DateValue(row, "joined_at", record.UpdatedAtUtc);
                entity.ApprovedAtUtc = NullableDate(row, "approved_at");
                entity.LastSeenUtc = NullableDate(row, "last_seen_at");
                entity.DownloadStatus = EnumValue(row, "download_status", DownloadStatus.NotStarted);
                entity.SubmissionStatus = EnumValue(row, "submission_status", SubmissionStatus.NotStarted);
                entity.ExtraTimeMinutes = IntValue(row, "extra_time_minutes");
                entity.ResubmitAllowed = BoolValue(row, "resubmit_allowed");
                entity.ResubmitReason = NullableString(row, "resubmit_reason");
                entity.CapabilityJson = RawOrNull(row, "capability_json");
                Stamp(entity, record);
                return entity.Id;
            }
            case "public_device_connections":
            {
                var entity = await db.PublicDeviceConnectionsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new PublicDeviceConnection { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.PublicDeviceConnectionsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.ParticipantId = GuidValue(row, "participant_id");
                entity.UserId = GuidValue(row, "user_id");
                entity.DeviceId = StringValue(row, "device_id");
                entity.ConnectionState = EnumValue<ConnectionState>(row, "connection_state");
                entity.HeartbeatAtUtc = DateValue(row, "heartbeat_at", record.UpdatedAtUtc);
                entity.ForegroundApplication = NullableString(row, "foreground_application");
                entity.RunningProcessSummaryJson = RawOrNull(row, "running_process_summary");
                entity.PolicyState = NullableString(row, "policy_state");
                entity.LockState = NullableString(row, "lock_state");
                entity.ViolationCount = IntValue(row, "violation_count");
                entity.AppVersion = NullableString(row, "app_version");
                entity.AgentVersion = NullableString(row, "agent_version");
                entity.PolicyLeaseExpiresAtUtc = NullableDate(row, "policy_lease_expires_at");
                entity.LastPolicyRenewalAtUtc = NullableDate(row, "last_policy_renewal_at");
                Stamp(entity, record);
                return entity.Id;
            }
            case "public_device_commands":
            {
                var entity = await db.PublicDeviceCommandsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new PublicDeviceCommand { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.PublicDeviceCommandsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.DeviceId = StringValue(row, "device_id");
                entity.CommandType = EnumValue<DeviceCommandType>(row, "command_type");
                entity.PayloadJson = RawOrNull(row, "payload") ?? "{}";
                entity.IssuedAtUtc = DateValue(row, "created_at", record.UpdatedAtUtc);
                entity.ExpiresAtUtc = DateValue(row, "expires_at", record.UpdatedAtUtc);
                entity.IssuedBy = GuidValue(row, "issued_by");
                entity.Signature = StringValue(row, "signature");
                entity.RetryCount = IntValue(row, "retry_count");
                entity.LastRetryAtUtc = NullableDate(row, "last_retry_at");
                Stamp(entity, record);
                return entity.Id;
            }
            case "public_device_command_results":
            {
                var entity = await db.PublicDeviceCommandResultsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new PublicDeviceCommandResult { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.PublicDeviceCommandResultsSet.Add(entity);
                entity.DeviceId = StringValue(row, "device_id");
                entity.Status = EnumValue<DeviceCommandStatus>(row, "status");
                entity.ReceivedAtUtc = DateValue(row, "received_at", record.UpdatedAtUtc);
                entity.ExecutedAtUtc = NullableDate(row, "executed_at");
                entity.ErrorCode = NullableString(row, "error_code");
                entity.ErrorMessage = NullableString(row, "error_message");
                Stamp(entity, record);
                return entity.Id;
            }
            case "submissions":
            {
                var entity = await db.SubmissionsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new Submission { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.SubmissionsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.ParticipantId = GuidValue(row, "participant_id");
                entity.AttemptNumber = IntValue(row, "attempt_number");
                entity.IdempotencyKey = NullableString(row, "idempotency_key") ?? string.Empty;
                entity.Status = EnumValue<SubmissionStatus>(row, "status");
                entity.ClientSubmittedAtUtc = DateValue(row, "client_submitted_at", record.UpdatedAtUtc);
                entity.ServerReceivedAtUtc = NullableDate(row, "server_received_at");
                entity.DeadlineUtc = DateValue(row, "deadline_at", record.UpdatedAtUtc);
                entity.IsLate = BoolValue(row, "is_late");
                entity.IsOfficial = BoolValue(row, "is_official");
                entity.ReceiptCode = NullableString(row, "receipt_code");
                entity.ReceiptSignature = NullableString(row, "receipt_signature");
                entity.TeacherRejectReason = NullableString(row, "teacher_reject_reason");
                entity.ClientNote = NullableString(row, "client_note");
                Stamp(entity, record);
                return entity.Id;
            }
            case "submission_files":
            {
                var entity = await db.SubmissionFilesSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new SubmissionFile { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.SubmissionFilesSet.Add(entity);
                entity.SubmissionId = GuidValue(row, "submission_id");
                entity.ClientFileId = NullableString(row, "client_file_id") ?? id.ToString("N");
                entity.OriginalName = StringValue(row, "name");
                entity.StoredName = NullableString(row, "stored_name") ?? entity.OriginalName;
                entity.MimeType = NullableString(row, "mime_type") ?? "application/octet-stream";
                entity.SizeBytes = LongValue(row, "size_bytes");
                entity.Sha256 = StringValue(row, "sha256");
                entity.TransferStatus = EnumValue(row, "transfer_status", TransferStatus.Queued);
                entity.SyncStatus = SyncStatus.Synced;
                entity.CloudObjectPath = NullableString(row, "cloud_object_path");
                Stamp(entity, record);
                return entity.Id;
            }
            case "violations":
            {
                var entity = await db.ViolationsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                entity ??= new Violation { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.ViolationsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.ParticipantId = GuidValue(row, "participant_id");
                entity.Type = StringValue(row, "type");
                entity.Severity = EnumValue<ViolationSeverity>(row, "severity");
                entity.PayloadJson = RawOrNull(row, "payload_json");
                entity.OccurredAtUtc = DateValue(row, "occurred_at", record.UpdatedAtUtc);
                entity.HandledAtUtc = NullableDate(row, "handled_at");
                entity.HandledBy = NullableGuid(row, "handled_by");
                Stamp(entity, record);
                return entity.Id;
            }
            case "quiz_attempts":
            {
                var incomingStatus = EnumValue<QuizAttemptStatus>(row, "status");
                var entity = await db.QuizAttemptsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                if (entity?.Status == QuizAttemptStatus.Finalized
                    && incomingStatus != QuizAttemptStatus.Finalized)
                    return entity.Id;
                entity ??= new QuizAttempt { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.QuizAttemptsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.ParticipantId = GuidValue(row, "participant_id");
                entity.AttemptNumber = IntValue(row, "attempt_number");
                entity.ExamVersion = IntValue(row, "exam_version");
                entity.Status = incomingStatus;
                entity.StartedAtUtc = DateValue(row, "started_at", record.UpdatedAtUtc);
                entity.DeadlineUtc = DateValue(row, "deadline_at", record.UpdatedAtUtc);
                entity.FinalizedAtUtc = NullableDate(row, "finalized_at");
                entity.AutoScore = NullableDecimal(row, "auto_score");
                entity.Score = NullableDecimal(row, "score");
                entity.MaxScore = DecimalValue(row, "max_score");
                entity.GradingStatus = EnumValue(
                    row,
                    "grading_status",
                    incomingStatus == QuizAttemptStatus.Finalized
                        ? GradingStatus.Graded
                        : GradingStatus.InProgress);
                entity.GeneralComment = NullableString(row, "general_comment");
                entity.GraderId = NullableGuid(row, "grader_id");
                entity.GradedAtUtc = NullableDate(row, "graded_at");
                entity.ReturnedAtUtc = NullableDate(row, "returned_at");
                entity.ResultPolicySnapshot = EnumValue(
                    row,
                    "result_policy",
                    QuizResultPolicy.Hidden);
                entity.SnapshotJson = RawOrNull(row, "snapshot_json") ?? "{}";
                entity.FinalizeIdempotencyKey = NullableString(row, "finalize_idempotency_key");
                Stamp(entity, record);
                return entity.Id;
            }
            case "quiz_answers":
            {
                var revision = LongValue(row, "revision");
                var attemptId = GuidValue(row, "attempt_id");
                var entity = await db.QuizAnswersSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return entity.Id;
                if (entity is not null && revision <= entity.Revision) return entity.Id;
                if (entity is not null
                    && await db.QuizAttemptsSet.AnyAsync(
                        x => x.Id == attemptId && x.Status == QuizAttemptStatus.Finalized,
                        cancellationToken))
                    throw new JsonException("A finalized quiz attempt answer cannot be revised.");
                entity ??= new QuizAnswer { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.QuizAnswersSet.Add(entity);
                entity.AttemptId = attemptId;
                entity.QuestionId = GuidValue(row, "question_id");
                entity.ChoiceIdsJson = RawOrNull(row, "choice_ids") ?? "[]";
                entity.Revision = revision;
                entity.ClientUpdatedAtUtc = DateValue(row, "client_updated_at", record.UpdatedAtUtc);
                Stamp(entity, record);
                return entity.Id;
            }
        }
        return id;
    }

    private static void Stamp(object entity, CloudPullRecord record)
    {
        switch (entity)
        {
            case ClassEnrollmentRequest value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case ClassMember value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case SessionParticipant value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case PublicDeviceConnection value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case PublicDeviceCommand value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case PublicDeviceCommandResult value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case Violation value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case Submission value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case SubmissionFile value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case QuizAttempt value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            case QuizAnswer value:
                value.SourceMode = "PublicCloud";
                value.CloudVersion = record.CloudVersion;
                value.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                value.CloudSyncState = "Pulled";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entity));
        }
    }

    private static string StringValue(JsonElement row, string name) =>
        NullableString(row, name) ?? throw new JsonException($"Required field {name} is missing.");
    private static string? NullableString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static Guid GuidValue(JsonElement row, string name) => Guid.Parse(StringValue(row, name));
    private static Guid? NullableGuid(JsonElement row, string name) =>
        Guid.TryParse(NullableString(row, name), out var value) ? value : null;
    private static DateTimeOffset DateValue(JsonElement row, string name, DateTimeOffset fallback) =>
        NullableDate(row, name) ?? fallback;
    private static DateTimeOffset? NullableDate(JsonElement row, string name) =>
        DateTimeOffset.TryParse(NullableString(row, name), out var value) ? value : null;
    private static int IntValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long LongValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static decimal DecimalValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result) ? result : 0;
    private static decimal? NullableDecimal(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null
        && value.TryGetDecimal(out var result)
            ? result
            : null;
    private static bool BoolValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? RawOrNull(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetRawText() : null;
    private static T EnumValue<T>(JsonElement row, string name, T fallback = default) where T : struct, Enum =>
        Enum.TryParse<T>(NullableString(row, name), true, out var value) ? value : fallback;

    private static bool JsonEquivalent(string left, string right)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return string.Equals(left, right, StringComparison.Ordinal); }
    }

    private static string Classify(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "auth",
        HttpRequestException => "network",
        JsonException => "validation",
        DbUpdateConcurrencyException => "conflict",
        _ when exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase) => "schema",
        _ => "unexpected"
    };

    private static async Task RecordFailureAsync(
        AppDbContext db,
        string entityName,
        string? cloudEntityId,
        string errorClass,
        string message,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var matchingFailures = await db.PublicCloudPullFailuresSet
            .Where(x => x.EntityName == entityName
                && x.CloudEntityId == cloudEntityId && x.ResolvedAtUtc == null)
            .ToListAsync(cancellationToken);
        var failure = matchingFailures
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefault();
        if (failure is null)
        {
            failure = new PublicCloudPullFailure
            {
                EntityName = entityName,
                CloudEntityId = cloudEntityId,
                ErrorClass = errorClass,
                ErrorMessage = message,
                PayloadJson = payloadJson
            };
            db.PublicCloudPullFailuresSet.Add(failure);
        }
        else
        {
            failure.ErrorClass = errorClass;
            failure.ErrorMessage = message;
            failure.PayloadJson = payloadJson ?? failure.PayloadJson;
        }
        failure.RetryCount++;
        failure.NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(
            RetrySeconds[Math.Min(failure.RetryCount - 1, RetrySeconds.Length - 1)]);
        await db.SaveChangesAsync(cancellationToken);
    }
}
