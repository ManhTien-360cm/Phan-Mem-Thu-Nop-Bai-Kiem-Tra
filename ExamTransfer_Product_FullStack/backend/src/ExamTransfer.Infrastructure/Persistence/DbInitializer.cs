using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Persistence;

public static class DbInitializer
{
    public const string SchemaVersion = "12";

    public static async Task InitializeAsync(AppDbContext db, IStoragePaths paths, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await ApplyCompatibilityMigrationsAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);

        var schema = await db.AppSettingsSet.FirstOrDefaultAsync(x => x.Key == "schema.version", cancellationToken);
        if (schema is null)
        {
            db.AppSettingsSet.Add(new AppSetting
            {
                Key = "schema.version",
                ValueJson = $"\"{SchemaVersion}\""
            });
        }
        else
        {
            schema.ValueJson = $"\"{SchemaVersion}\"";
        }

        if (!await db.UsersSet.AnyAsync(cancellationToken))
        {
            db.UsersSet.Add(new User
            {
                Username = "admin",
                DisplayName = "Quản trị viên cục bộ",
                Role = UserRole.Admin,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
    private static async Task ApplyCompatibilityMigrationsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "sync_queue", "CloudObjectPath", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "UploadUrl", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "UploadOffset", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "LastAttemptAtUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "CompletedAtUtc", "TEXT NULL", cancellationToken);

        await EnsureColumnAsync(db, "graded_attachments", "SyncStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "graded_attachments", "CloudObjectPath", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "grades", "SourceMode", "TEXT NOT NULL DEFAULT 'Lan'", cancellationToken);
        await EnsureColumnAsync(db, "grades", "CloudVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "grades", "CloudUpdatedAtUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "grades", "CloudSyncState", "TEXT NOT NULL DEFAULT 'LocalOnly'", cancellationToken);
        await EnsureColumnAsync(db, "grades", "Revision", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureEssayGradingTablesAsync(db, cancellationToken);
        await EnsureColumnAsync(db, "export_jobs", "SyncStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "export_jobs", "CloudObjectPath", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "backups", "SyncStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "backups", "CloudObjectPath", "TEXT NULL", cancellationToken);

        await EnsureColumnAsync(db, "users", "OrganizationId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "users", "DateOfBirth", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "users", "MustChangePassword", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "user_login_sessions", "OrganizationId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "user_login_sessions", "EncryptedRefreshToken", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "exams", "DeliveryType", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exams", "QuizResultPolicy", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exams", "SupervisionMode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "classes", "AccessMode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "classes", "EnrollmentOpen", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "classes", "RequireEnrollmentApproval", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "classes", "EnrollmentCodeHash", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "classes", "EnrollmentOpenedAtUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "classes", "EnrollmentClosedAtUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "classes", "PublicVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exam_sessions", "AccessMode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exam_sessions", "AdmissionMode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exam_sessions", "DeliveryTypeSnapshot", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exam_sessions", "SupervisionModeSnapshot", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exam_sessions", "QuizResultPolicySnapshot", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "exam_sessions", "ExamVersionSnapshot", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "exam_sessions"
            SET "DeliveryTypeSnapshot" = COALESCE((SELECT "DeliveryType" FROM "exams" WHERE "exams"."Id" = "exam_sessions"."ExamId"), 0),
                "SupervisionModeSnapshot" = COALESCE((SELECT "SupervisionMode" FROM "exams" WHERE "exams"."Id" = "exam_sessions"."ExamId"), 0),
                "QuizResultPolicySnapshot" = COALESCE((SELECT "QuizResultPolicy" FROM "exams" WHERE "exams"."Id" = "exam_sessions"."ExamId"), 0),
                "ExamVersionSnapshot" = COALESCE((SELECT "Version" FROM "exams" WHERE "exams"."Id" = "exam_sessions"."ExamId"), 1)
            WHERE "ExamVersionSnapshot" = 1
              AND "DeliveryTypeSnapshot" = 0
              AND "SupervisionModeSnapshot" = 0
              AND "QuizResultPolicySnapshot" = 0;
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "exam_sessions"
            SET "AdmissionMode" = 1
            WHERE "ClassId" IS NULL
              AND "AdmissionMode" = 0;
            """, cancellationToken);
        await EnsureQuizTablesAsync(db, cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "AttemptNumber", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "ResultPolicySnapshot", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "AutoScore", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "GradingStatus", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "GeneralComment", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "GraderId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "GradedAtUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "quiz_attempts", "ReturnedAtUtc", "TEXT NULL", cancellationToken);
        await EnsureQuizGradingTablesAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "quiz_attempts"
            SET "AutoScore" = "Score",
                "MaxScore" = '10.0',
                "GradingStatus" = CASE WHEN "Status" = 1 THEN 2 ELSE 1 END,
                "GradedAtUtc" = CASE WHEN "Status" = 1 THEN "FinalizedAtUtc" ELSE NULL END
            WHERE "AutoScore" IS NULL;
            """, cancellationToken);
        await EnsureQuizImportTablesAsync(db, cancellationToken);
        foreach (var table in new[] { "class_members", "session_participants", "submissions", "submission_files", "violations", "quiz_attempts", "quiz_answers" })
        {
            await EnsureColumnAsync(db, table, "SourceMode", "TEXT NOT NULL DEFAULT 'Lan'", cancellationToken);
            await EnsureColumnAsync(db, table, "CloudVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(db, table, "CloudUpdatedAtUtc", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, table, "CloudSyncState", "TEXT NOT NULL DEFAULT 'LocalOnly'", cancellationToken);
        }
        await EnsurePublicCloudProjectionTablesAsync(db, cancellationToken);
        await EnsurePublicCloudReplicaTablesAsync(db, cancellationToken);
    }

    private static async Task EnsurePublicCloudProjectionTablesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "class_enrollment_requests" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_class_enrollment_requests" PRIMARY KEY,
                "SourceMode" TEXT NOT NULL, "CloudVersion" INTEGER NOT NULL,
                "CloudUpdatedAtUtc" TEXT NULL, "CloudSyncState" TEXT NOT NULL,
                "ClassId" TEXT NOT NULL, "StudentUserId" TEXT NOT NULL,
                "StudentCode" TEXT NOT NULL, "Status" TEXT NOT NULL,
                "RequestedAtUtc" TEXT NOT NULL, "DecidedAtUtc" TEXT NULL,
                "DecidedBy" TEXT NULL, "DecisionReason" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_class_enrollment_requests_ClassId_StudentUserId"
                ON "class_enrollment_requests" ("ClassId", "StudentUserId");
            CREATE INDEX IF NOT EXISTS "IX_class_enrollment_requests_ClassId_Status_RequestedAtUtc"
                ON "class_enrollment_requests" ("ClassId", "Status", "RequestedAtUtc");

            CREATE TABLE IF NOT EXISTS "public_device_connections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_device_connections" PRIMARY KEY,
                "SourceMode" TEXT NOT NULL, "CloudVersion" INTEGER NOT NULL,
                "CloudUpdatedAtUtc" TEXT NULL, "CloudSyncState" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL, "ParticipantId" TEXT NOT NULL, "UserId" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL, "ConnectionState" INTEGER NOT NULL,
                "HeartbeatAtUtc" TEXT NOT NULL, "ForegroundApplication" TEXT NULL,
                "RunningProcessSummaryJson" TEXT NULL, "PolicyState" TEXT NULL,
                "LockState" TEXT NULL, "ViolationCount" INTEGER NOT NULL,
                "AppVersion" TEXT NULL, "AgentVersion" TEXT NULL,
                "PolicyLeaseExpiresAtUtc" TEXT NULL, "LastPolicyRenewalAtUtc" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_public_device_connections_SessionId_DeviceId"
                ON "public_device_connections" ("SessionId", "DeviceId");

            CREATE TABLE IF NOT EXISTS "public_device_commands" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_device_commands" PRIMARY KEY,
                "SourceMode" TEXT NOT NULL, "CloudVersion" INTEGER NOT NULL,
                "CloudUpdatedAtUtc" TEXT NULL, "CloudSyncState" TEXT NOT NULL,
                "SessionId" TEXT NOT NULL, "DeviceId" TEXT NOT NULL,
                "CommandType" INTEGER NOT NULL, "PayloadJson" TEXT NOT NULL,
                "IssuedAtUtc" TEXT NOT NULL, "ExpiresAtUtc" TEXT NOT NULL,
                "IssuedBy" TEXT NOT NULL, "Signature" TEXT NOT NULL,
                "RetryCount" INTEGER NOT NULL, "LastRetryAtUtc" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_public_device_commands_SessionId_DeviceId_IssuedAtUtc"
                ON "public_device_commands" ("SessionId", "DeviceId", "IssuedAtUtc");

            CREATE TABLE IF NOT EXISTS "public_device_command_results" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_device_command_results" PRIMARY KEY,
                "SourceMode" TEXT NOT NULL, "CloudVersion" INTEGER NOT NULL,
                "CloudUpdatedAtUtc" TEXT NULL, "CloudSyncState" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL, "Status" INTEGER NOT NULL,
                "ReceivedAtUtc" TEXT NOT NULL, "ExecutedAtUtc" TEXT NULL,
                "ErrorCode" TEXT NULL, "ErrorMessage" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_public_device_command_results_DeviceId_ReceivedAtUtc"
                ON "public_device_command_results" ("DeviceId", "ReceivedAtUtc");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureEssayGradingTablesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "essay_grade_mutation_receipts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_essay_grade_mutation_receipts" PRIMARY KEY,
                "SubmissionId" TEXT NOT NULL,
                "ActorId" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "RequestHash" TEXT NOT NULL,
                "ResultJson" TEXT NOT NULL,
                "EventId" TEXT NULL,
                "GradeRevision" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "RowVersion" TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_essay_grade_mutation_receipts_SubmissionId_CreatedAtUtc"
                ON "essay_grade_mutation_receipts" ("SubmissionId", "CreatedAtUtc");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureQuizGradingTablesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "quiz_grade_mutation_receipts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_grade_mutation_receipts" PRIMARY KEY,
                "AttemptId" TEXT NOT NULL,
                "ActorId" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "RequestHash" TEXT NOT NULL,
                "ResultJson" TEXT NOT NULL,
                "EventId" TEXT NULL,
                "AttemptRowVersion" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "RowVersion" TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_quiz_grade_mutation_receipts_AttemptId_CreatedAtUtc"
                ON "quiz_grade_mutation_receipts" ("AttemptId", "CreatedAtUtc");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsurePublicCloudReplicaTablesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "public_cloud_pull_cursors" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_cloud_pull_cursors" PRIMARY KEY,
                "EntityName" TEXT NOT NULL, "LastCloudVersion" INTEGER NOT NULL,
                "LastUpdatedAtUtc" TEXT NULL, "LastEntityId" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_public_cloud_pull_cursors_EntityName"
                ON "public_cloud_pull_cursors" ("EntityName");
            CREATE TABLE IF NOT EXISTS "public_cloud_replica_records" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_cloud_replica_records" PRIMARY KEY,
                "EntityName" TEXT NOT NULL, "CloudEntityId" TEXT NOT NULL,
                "CloudVersion" INTEGER NOT NULL, "CloudUpdatedAtUtc" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_public_cloud_replica_records_EntityName_CloudEntityId"
                ON "public_cloud_replica_records" ("EntityName", "CloudEntityId");
            CREATE INDEX IF NOT EXISTS "IX_public_cloud_replica_records_EntityName_CloudVersion"
                ON "public_cloud_replica_records" ("EntityName", "CloudVersion");
            CREATE TABLE IF NOT EXISTS "public_cloud_id_mappings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_cloud_id_mappings" PRIMARY KEY,
                "EntityName" TEXT NOT NULL, "CloudEntityId" TEXT NOT NULL, "LocalEntityId" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_public_cloud_id_mappings_EntityName_CloudEntityId"
                ON "public_cloud_id_mappings" ("EntityName", "CloudEntityId");
            CREATE TABLE IF NOT EXISTS "public_cloud_pull_failures" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_public_cloud_pull_failures" PRIMARY KEY,
                "EntityName" TEXT NOT NULL, "CloudEntityId" TEXT NULL,
                "ErrorClass" TEXT NOT NULL, "ErrorMessage" TEXT NOT NULL, "PayloadJson" TEXT NULL,
                "RetryCount" INTEGER NOT NULL, "NextRetryAtUtc" TEXT NULL, "ResolvedAtUtc" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_public_cloud_pull_failures_ResolvedAtUtc_NextRetryAtUtc"
                ON "public_cloud_pull_failures" ("ResolvedAtUtc", "NextRetryAtUtc");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureQuizTablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "quiz_questions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_questions" PRIMARY KEY,
                "ExamId" TEXT NOT NULL, "Version" INTEGER NOT NULL, "Order" INTEGER NOT NULL,
                "Text" TEXT NOT NULL, "Points" TEXT NOT NULL, "Multiple" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL,
                CONSTRAINT "FK_quiz_questions_exams_ExamId" FOREIGN KEY ("ExamId") REFERENCES "exams" ("Id") ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_quiz_questions_ExamId_Version_Order" ON "quiz_questions" ("ExamId", "Version", "Order");
            CREATE TABLE IF NOT EXISTS "quiz_choices" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_choices" PRIMARY KEY,
                "QuestionId" TEXT NOT NULL, "Order" INTEGER NOT NULL, "Text" TEXT NOT NULL, "IsCorrect" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL,
                CONSTRAINT "FK_quiz_choices_quiz_questions_QuestionId" FOREIGN KEY ("QuestionId") REFERENCES "quiz_questions" ("Id") ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_quiz_choices_QuestionId_Order" ON "quiz_choices" ("QuestionId", "Order");
            CREATE TABLE IF NOT EXISTS "quiz_attempts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_attempts" PRIMARY KEY,
                "SessionId" TEXT NOT NULL, "ParticipantId" TEXT NOT NULL, "AttemptNumber" INTEGER NOT NULL DEFAULT 1, "ExamVersion" INTEGER NOT NULL, "Status" INTEGER NOT NULL,
                "StartedAtUtc" TEXT NOT NULL, "DeadlineUtc" TEXT NOT NULL, "FinalizedAtUtc" TEXT NULL,
                "Score" TEXT NULL, "MaxScore" TEXT NOT NULL, "SnapshotJson" TEXT NOT NULL, "FinalizeIdempotencyKey" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL,
                CONSTRAINT "FK_quiz_attempts_exam_sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "exam_sessions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_quiz_attempts_session_participants_ParticipantId" FOREIGN KEY ("ParticipantId") REFERENCES "session_participants" ("Id") ON DELETE RESTRICT);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_quiz_attempts_SessionId_ParticipantId" ON "quiz_attempts" ("SessionId", "ParticipantId");
            CREATE TABLE IF NOT EXISTS "quiz_answers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_answers" PRIMARY KEY,
                "AttemptId" TEXT NOT NULL, "QuestionId" TEXT NOT NULL, "ChoiceIdsJson" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL, "ClientUpdatedAtUtc" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL,
                CONSTRAINT "FK_quiz_answers_quiz_attempts_AttemptId" FOREIGN KEY ("AttemptId") REFERENCES "quiz_attempts" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_quiz_answers_quiz_questions_QuestionId" FOREIGN KEY ("QuestionId") REFERENCES "quiz_questions" ("Id") ON DELETE RESTRICT);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_quiz_answers_AttemptId_QuestionId" ON "quiz_answers" ("AttemptId", "QuestionId");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureQuizImportTablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "quiz_import_sources" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_import_sources" PRIMARY KEY,
                "ExamId" TEXT NOT NULL, "ExamVersion" INTEGER NOT NULL,
                "OriginalName" TEXT NOT NULL, "MimeType" TEXT NOT NULL,
                "SizeBytes" INTEGER NOT NULL, "Sha256" TEXT NOT NULL,
                "RelativePath" TEXT NOT NULL, "Status" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL, "ImportedAtUtc" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL,
                CONSTRAINT "FK_quiz_import_sources_exams_ExamId" FOREIGN KEY ("ExamId") REFERENCES "exams" ("Id") ON DELETE CASCADE);
            DELETE FROM "quiz_import_sources"
            WHERE "rowid" IN (
                SELECT "rowid"
                FROM (
                    SELECT "rowid",
                           ROW_NUMBER() OVER (
                               PARTITION BY "ExamId", "ExamVersion"
                               ORDER BY CASE WHEN "Status" = 'Committed' THEN 0 ELSE 1 END,
                                        "ImportedAtUtc" DESC,
                                        "UpdatedAtUtc" DESC,
                                        "Id" DESC
                           ) AS "duplicate_rank"
                    FROM "quiz_import_sources"
                )
                WHERE "duplicate_rank" > 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_quiz_import_sources_ExamId_ExamVersion"
                ON "quiz_import_sources" ("ExamId", "ExamVersion");

            CREATE TABLE IF NOT EXISTS "quiz_import_previews" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_quiz_import_previews" PRIMARY KEY,
                "TokenHash" TEXT NOT NULL, "ExamId" TEXT NOT NULL, "ExamVersion" INTEGER NOT NULL,
                "TeacherId" TEXT NOT NULL, "ExamRowVersion" TEXT NOT NULL,
                "OriginalName" TEXT NOT NULL, "MimeType" TEXT NOT NULL,
                "SizeBytes" INTEGER NOT NULL, "Sha256" TEXT NOT NULL,
                "TemporaryPath" TEXT NOT NULL, "DocumentJson" TEXT NOT NULL,
                "WarningsJson" TEXT NOT NULL, "ExpiresAtUtc" TEXT NOT NULL,
                "CommittedAtUtc" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL, "RowVersion" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_quiz_import_previews_TokenHash"
                ON "quiz_import_previews" ("TokenHash");
            CREATE INDEX IF NOT EXISTS "IX_quiz_import_previews_ExpiresAtUtc"
                ON "quiz_import_previews" ("ExpiresAtUtc");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        AppDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            var exists = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(
                        reader.GetString(1),
                        column,
                        StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            await reader.DisposeAsync();
            if (exists)
                return;

            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

}
