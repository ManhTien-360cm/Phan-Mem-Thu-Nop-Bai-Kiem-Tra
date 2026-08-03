using ExamTransfer.Application;
using ExamTransfer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ExamTransfer.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> UsersSet => Set<User>();
    public DbSet<UserLoginSession> UserLoginSessionsSet => Set<UserLoginSession>();
    public DbSet<ClassRoom> ClassesSet => Set<ClassRoom>();
    public DbSet<ClassMember> ClassMembersSet => Set<ClassMember>();
    public DbSet<ClassEnrollmentRequest> ClassEnrollmentRequestsSet => Set<ClassEnrollmentRequest>();
    public DbSet<Exam> ExamsSet => Set<Exam>();
    public DbSet<ExamFile> ExamFilesSet => Set<ExamFile>();
    public DbSet<QuizImportSource> QuizImportSourcesSet => Set<QuizImportSource>();
    public DbSet<QuizImportPreview> QuizImportPreviewsSet => Set<QuizImportPreview>();
    public DbSet<QuizQuestion> QuizQuestionsSet => Set<QuizQuestion>();
    public DbSet<QuizChoice> QuizChoicesSet => Set<QuizChoice>();
    public DbSet<QuizAttempt> QuizAttemptsSet => Set<QuizAttempt>();
    public DbSet<QuizAnswer> QuizAnswersSet => Set<QuizAnswer>();
    public DbSet<QuizGradeMutationReceipt> QuizGradeMutationReceiptsSet => Set<QuizGradeMutationReceipt>();
    public DbSet<ExamSession> ExamSessionsSet => Set<ExamSession>();
    public DbSet<SessionParticipant> SessionParticipantsSet => Set<SessionParticipant>();
    public DbSet<ParticipantExtraTime> ParticipantExtraTimesSet => Set<ParticipantExtraTime>();
    public DbSet<Message> MessagesSet => Set<Message>();
    public DbSet<Submission> SubmissionsSet => Set<Submission>();
    public DbSet<SubmissionFile> SubmissionFilesSet => Set<SubmissionFile>();
    public DbSet<Grade> GradesSet => Set<Grade>();
    public DbSet<EssayGradeMutationReceipt> EssayGradeMutationReceiptsSet => Set<EssayGradeMutationReceipt>();
    public DbSet<RubricScore> RubricScoresSet => Set<RubricScore>();
    public DbSet<GradedAttachment> GradedAttachmentsSet => Set<GradedAttachment>();
    public DbSet<ControlPolicy> ControlPoliciesSet => Set<ControlPolicy>();
    public DbSet<DevicePolicyStatus> DevicePolicyStatusesSet => Set<DevicePolicyStatus>();
    public DbSet<PublicDeviceConnection> PublicDeviceConnectionsSet => Set<PublicDeviceConnection>();
    public DbSet<PublicDeviceCommand> PublicDeviceCommandsSet => Set<PublicDeviceCommand>();
    public DbSet<PublicDeviceCommandResult> PublicDeviceCommandResultsSet => Set<PublicDeviceCommandResult>();
    public DbSet<Violation> ViolationsSet => Set<Violation>();
    public DbSet<AuditLog> AuditLogsSet => Set<AuditLog>();
    public DbSet<ExportJob> ExportJobsSet => Set<ExportJob>();
    public DbSet<BackupRecord> BackupsSet => Set<BackupRecord>();
    public DbSet<SyncQueueItem> SyncQueueSet => Set<SyncQueueItem>();
    public DbSet<PublicCloudPullCursor> PublicCloudPullCursorsSet => Set<PublicCloudPullCursor>();
    public DbSet<PublicCloudReplicaRecord> PublicCloudReplicaRecordsSet => Set<PublicCloudReplicaRecord>();
    public DbSet<PublicCloudIdMapping> PublicCloudIdMappingsSet => Set<PublicCloudIdMapping>();
    public DbSet<PublicCloudPullFailure> PublicCloudPullFailuresSet => Set<PublicCloudPullFailure>();
    public DbSet<AppSetting> AppSettingsSet => Set<AppSetting>();

    IQueryable<User> IAppDbContext.Users => UsersSet;
    IQueryable<UserLoginSession> IAppDbContext.UserLoginSessions => UserLoginSessionsSet;
    IQueryable<ClassRoom> IAppDbContext.Classes => ClassesSet;
    IQueryable<ClassMember> IAppDbContext.ClassMembers => ClassMembersSet;
    IQueryable<Exam> IAppDbContext.Exams => ExamsSet;
    IQueryable<ExamFile> IAppDbContext.ExamFiles => ExamFilesSet;
    IQueryable<ExamSession> IAppDbContext.ExamSessions => ExamSessionsSet;
    IQueryable<SessionParticipant> IAppDbContext.SessionParticipants => SessionParticipantsSet;
    IQueryable<ParticipantExtraTime> IAppDbContext.ParticipantExtraTimes => ParticipantExtraTimesSet;
    IQueryable<Message> IAppDbContext.Messages => MessagesSet;
    IQueryable<Submission> IAppDbContext.Submissions => SubmissionsSet;
    IQueryable<SubmissionFile> IAppDbContext.SubmissionFiles => SubmissionFilesSet;
    IQueryable<Grade> IAppDbContext.Grades => GradesSet;
    IQueryable<RubricScore> IAppDbContext.RubricScores => RubricScoresSet;
    IQueryable<GradedAttachment> IAppDbContext.GradedAttachments => GradedAttachmentsSet;
    IQueryable<ControlPolicy> IAppDbContext.ControlPolicies => ControlPoliciesSet;
    IQueryable<DevicePolicyStatus> IAppDbContext.DevicePolicyStatuses => DevicePolicyStatusesSet;
    IQueryable<Violation> IAppDbContext.Violations => ViolationsSet;
    IQueryable<AuditLog> IAppDbContext.AuditLogs => AuditLogsSet;
    IQueryable<ExportJob> IAppDbContext.ExportJobs => ExportJobsSet;
    IQueryable<BackupRecord> IAppDbContext.Backups => BackupsSet;
    IQueryable<SyncQueueItem> IAppDbContext.SyncQueue => SyncQueueSet;
    IQueryable<AppSetting> IAppDbContext.AppSettings => AppSettingsSet;

    void IAppDbContext.Add<TEntity>(TEntity entity) => Set<TEntity>().Add(entity);
    void IAppDbContext.AddRange<TEntity>(IEnumerable<TEntity> entities) => Set<TEntity>().AddRange(entities);
    void IAppDbContext.Remove<TEntity>(TEntity entity) => Set<TEntity>().Remove(entity);

    public async Task<IAppTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        new EfTransaction(await Database.BeginTransactionAsync(cancellationToken));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(x => x.Username).IsUnique();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.StudentCode).IsUnique();
            entity.HasIndex(x => x.SupabaseAuthUserId).IsUnique();
            entity.HasIndex(x => x.OrganizationId);
        });
        modelBuilder.Entity<UserLoginSession>(entity =>
        {
            entity.ToTable("user_login_sessions");
            entity.HasIndex(x => x.SessionTokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAtUtc });
            entity.HasIndex(x => new { x.OrganizationId, x.UserId });
            entity.HasOne(x => x.User).WithMany(x => x.LoginSessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        base.OnModelCreating(modelBuilder);

        ConfigureEntity<User>(modelBuilder, "users");
        ConfigureEntity<UserLoginSession>(modelBuilder, "user_login_sessions");
        ConfigureEntity<ClassRoom>(modelBuilder, "classes");
        ConfigureEntity<ClassMember>(modelBuilder, "class_members");
        ConfigureEntity<ClassEnrollmentRequest>(modelBuilder, "class_enrollment_requests");
        ConfigureEntity<Exam>(modelBuilder, "exams");
        ConfigureEntity<ExamFile>(modelBuilder, "exam_files");
        ConfigureEntity<QuizImportSource>(modelBuilder, "quiz_import_sources");
        ConfigureEntity<QuizImportPreview>(modelBuilder, "quiz_import_previews");
        ConfigureEntity<QuizQuestion>(modelBuilder, "quiz_questions");
        ConfigureEntity<QuizChoice>(modelBuilder, "quiz_choices");
        ConfigureEntity<QuizAttempt>(modelBuilder, "quiz_attempts");
        ConfigureEntity<QuizAnswer>(modelBuilder, "quiz_answers");
        ConfigureEntity<QuizGradeMutationReceipt>(modelBuilder, "quiz_grade_mutation_receipts");
        ConfigureEntity<ExamSession>(modelBuilder, "exam_sessions");
        ConfigureEntity<SessionParticipant>(modelBuilder, "session_participants");
        ConfigureEntity<ParticipantExtraTime>(modelBuilder, "participant_extra_time");
        ConfigureEntity<Message>(modelBuilder, "messages");
        ConfigureEntity<Submission>(modelBuilder, "submissions");
        ConfigureEntity<SubmissionFile>(modelBuilder, "submission_files");
        ConfigureEntity<Grade>(modelBuilder, "grades");
        ConfigureEntity<EssayGradeMutationReceipt>(modelBuilder, "essay_grade_mutation_receipts");
        ConfigureEntity<RubricScore>(modelBuilder, "rubric_scores");
        ConfigureEntity<GradedAttachment>(modelBuilder, "graded_attachments");
        ConfigureEntity<ControlPolicy>(modelBuilder, "control_policies");
        ConfigureEntity<DevicePolicyStatus>(modelBuilder, "device_policy_status");
        ConfigureEntity<PublicDeviceConnection>(modelBuilder, "public_device_connections");
        ConfigureEntity<PublicDeviceCommand>(modelBuilder, "public_device_commands");
        ConfigureEntity<PublicDeviceCommandResult>(modelBuilder, "public_device_command_results");
        ConfigureEntity<Violation>(modelBuilder, "violations");
        ConfigureEntity<AuditLog>(modelBuilder, "audit_logs");
        ConfigureEntity<ExportJob>(modelBuilder, "export_jobs");
        ConfigureEntity<BackupRecord>(modelBuilder, "backups");
        ConfigureEntity<SyncQueueItem>(modelBuilder, "sync_queue");
        ConfigureEntity<PublicCloudPullCursor>(modelBuilder, "public_cloud_pull_cursors");
        ConfigureEntity<PublicCloudReplicaRecord>(modelBuilder, "public_cloud_replica_records");
        ConfigureEntity<PublicCloudIdMapping>(modelBuilder, "public_cloud_id_mappings");
        ConfigureEntity<PublicCloudPullFailure>(modelBuilder, "public_cloud_pull_failures");

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("app_settings");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
        });

        modelBuilder.Entity<User>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<User>().HasIndex(x => x.CloudId);
        modelBuilder.Entity<ClassRoom>().HasIndex(x => new { x.Code, x.SchoolYear }).IsUnique();
        modelBuilder.Entity<ClassMember>().HasIndex(x => new { x.ClassId, x.StudentCode }).IsUnique();
        modelBuilder.Entity<ClassEnrollmentRequest>().HasIndex(x => new { x.ClassId, x.StudentUserId }).IsUnique();
        modelBuilder.Entity<ClassEnrollmentRequest>().HasIndex(x => new { x.ClassId, x.Status, x.RequestedAtUtc });
        modelBuilder.Entity<Exam>().HasIndex(x => new { x.Status, x.ClassId });
        modelBuilder.Entity<ExamFile>().HasIndex(x => new { x.ExamId, x.Version, x.StoredName }).IsUnique();
        modelBuilder.Entity<QuizImportSource>().HasIndex(x => new { x.ExamId, x.ExamVersion }).IsUnique();
        modelBuilder.Entity<QuizImportPreview>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<QuizImportPreview>().HasIndex(x => x.ExpiresAtUtc);
        modelBuilder.Entity<QuizQuestion>().HasIndex(x => new { x.ExamId, x.Version, x.Order }).IsUnique();
        modelBuilder.Entity<QuizChoice>().HasIndex(x => new { x.QuestionId, x.Order }).IsUnique();
        modelBuilder.Entity<QuizAttempt>().HasIndex(x => new { x.SessionId, x.ParticipantId }).IsUnique();
        modelBuilder.Entity<QuizAnswer>().HasIndex(x => new { x.AttemptId, x.QuestionId }).IsUnique();
        modelBuilder.Entity<QuizGradeMutationReceipt>().HasIndex(x => new { x.AttemptId, x.CreatedAtUtc });
        modelBuilder.Entity<ExamSession>().HasIndex(x => x.RoomCode);
        modelBuilder.Entity<SessionParticipant>().HasIndex(x => new { x.SessionId, x.StudentCode }).IsUnique();
        modelBuilder.Entity<SessionParticipant>().HasIndex(x => x.LastSeenUtc);
        modelBuilder.Entity<Submission>().HasIndex(x => new { x.ParticipantId, x.AttemptNumber }).IsUnique();
        modelBuilder.Entity<Submission>().HasIndex(x => new { x.ParticipantId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<SubmissionFile>().HasIndex(x => x.SubmissionId);
        modelBuilder.Entity<Grade>().HasIndex(x => x.SubmissionId).IsUnique();
        modelBuilder.Entity<EssayGradeMutationReceipt>().HasIndex(x => new { x.SubmissionId, x.CreatedAtUtc });
        modelBuilder.Entity<RubricScore>().HasIndex(x => new { x.GradeId, x.CriterionKey }).IsUnique();
        modelBuilder.Entity<ControlPolicy>().HasIndex(x => new { x.SessionId, x.Version }).IsUnique();
        modelBuilder.Entity<DevicePolicyStatus>().HasIndex(x => new { x.ParticipantId, x.PolicyVersion }).IsUnique();
        modelBuilder.Entity<PublicDeviceConnection>().HasIndex(x => new { x.SessionId, x.DeviceId }).IsUnique();
        modelBuilder.Entity<PublicDeviceCommand>().HasIndex(x => new { x.SessionId, x.DeviceId, x.IssuedAtUtc });
        modelBuilder.Entity<PublicDeviceCommandResult>().HasIndex(x => new { x.DeviceId, x.ReceivedAtUtc });
        modelBuilder.Entity<Violation>().HasIndex(x => new { x.SessionId, x.ParticipantId, x.OccurredAtUtc });
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.CreatedAtUtc, x.EntityType, x.SessionId });
        modelBuilder.Entity<ExportJob>().HasIndex(x => new { x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<BackupRecord>().HasIndex(x => x.CreatedAtUtc);
        modelBuilder.Entity<SyncQueueItem>().HasIndex(x => new { x.Status, x.NextRetryAtUtc });
        modelBuilder.Entity<PublicCloudPullCursor>().HasIndex(x => x.EntityName).IsUnique();
        modelBuilder.Entity<PublicCloudReplicaRecord>().HasIndex(x => new { x.EntityName, x.CloudEntityId }).IsUnique();
        modelBuilder.Entity<PublicCloudReplicaRecord>().HasIndex(x => new { x.EntityName, x.CloudVersion });
        modelBuilder.Entity<PublicCloudIdMapping>().HasIndex(x => new { x.EntityName, x.CloudEntityId }).IsUnique();
        modelBuilder.Entity<PublicCloudPullFailure>().HasIndex(x => new { x.ResolvedAtUtc, x.NextRetryAtUtc });

        modelBuilder.Entity<ClassMember>().HasOne(x => x.Class).WithMany(x => x.Members).HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Exam>().HasOne(x => x.Class).WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExamFile>().HasOne(x => x.Exam).WithMany(x => x.Files).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizImportSource>().HasOne(x => x.Exam).WithMany(x => x.QuizImportSources).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizQuestion>().HasOne(x => x.Exam).WithMany(x => x.QuizQuestions).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizChoice>().HasOne(x => x.Question).WithMany(x => x.Choices).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizAttempt>().HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QuizAttempt>().HasOne(x => x.Participant).WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QuizAnswer>().HasOne(x => x.Attempt).WithMany(x => x.Answers).HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QuizAnswer>().HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExamSession>().HasOne(x => x.Exam).WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SessionParticipant>().HasOne(x => x.Session).WithMany(x => x.Participants).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ParticipantExtraTime>().HasOne(x => x.Participant).WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Message>().HasOne(x => x.Session).WithMany(x => x.Messages).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Submission>().HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Submission>().HasOne(x => x.Participant).WithMany(x => x.Submissions).HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SubmissionFile>().HasOne(x => x.Submission).WithMany(x => x.Files).HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Grade>().HasOne(x => x.Submission).WithOne().HasForeignKey<Grade>(x => x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RubricScore>().HasOne(x => x.Grade).WithMany(x => x.RubricScores).HasForeignKey(x => x.GradeId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GradedAttachment>().HasOne(x => x.Grade).WithMany(x => x.Attachments).HasForeignKey(x => x.GradeId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureEntity<TEntity>(ModelBuilder modelBuilder, string tableName) where TEntity : EntityBase
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAtUtc == default) entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.RowVersion = Guid.NewGuid().ToString("N");
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.RowVersion = Guid.NewGuid().ToString("N");
            }
        }

        foreach (var entry in ChangeTracker.Entries<AppSetting>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.UpdatedAtUtc = now;
            entry.Entity.RowVersion = Guid.NewGuid().ToString("N");
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    private sealed class EfTransaction(IDbContextTransaction transaction) : IAppTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
