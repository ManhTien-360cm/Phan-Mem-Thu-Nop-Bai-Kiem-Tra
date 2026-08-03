using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Domain;

public sealed class User : EntityBase
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public UserRole Role { get; set; }
    public string? PasswordHash { get; set; }
    public Guid? CloudId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? StudentCode { get; set; }
    public string? SupabaseAuthUserId { get; set; }
    public string? OrganizationId { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? LastLoginAtUtc { get; set; }
    public ICollection<UserLoginSession> LoginSessions { get; set; } = new List<UserLoginSession>();
}

public sealed class UserLoginSession : EntityBase
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string? OrganizationId { get; set; }
    public string SessionTokenHash { get; set; } = string.Empty;
    public string? EncryptedRefreshToken { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokeReason { get; set; }
    public bool IsActive => RevokedAtUtc is null && ExpiresAtUtc > DateTimeOffset.UtcNow;
}

public sealed class ClassRoom : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string SchoolYear { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ClassStatus Status { get; set; } = ClassStatus.Active;
    public ClassAccessMode AccessMode { get; set; } = ClassAccessMode.Private;
    public bool EnrollmentOpen { get; set; }
    public bool RequireEnrollmentApproval { get; set; } = true;
    public string? EnrollmentCodeHash { get; set; }
    public DateTimeOffset? EnrollmentOpenedAtUtc { get; set; }
    public DateTimeOffset? EnrollmentClosedAtUtc { get; set; }
    public int PublicVersion { get; set; }
    public Guid? CreatedBy { get; set; }
    public ICollection<ClassMember> Members { get; set; } = new List<ClassMember>();
}

public sealed class ClassMember : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid ClassId { get; set; }
    public ClassRoom Class { get; set; } = null!;
    public Guid? UserId { get; set; }
    public string StudentCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class ClassEnrollmentRequest : EntityBase
{
    public string SourceMode { get; set; } = "PublicCloud";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "Pulled";
    public Guid ClassId { get; set; }
    public Guid StudentUserId { get; set; }
    public string StudentCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class Exam : EntityBase
{
    public Guid? ClassId { get; set; }
    public ClassRoom? Class { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DurationMinutes { get; set; }
    public ExamDeliveryType DeliveryType { get; set; } = ExamDeliveryType.FileSubmission;
    public QuizResultPolicy QuizResultPolicy { get; set; } = QuizResultPolicy.Hidden;
    public SupervisionMode SupervisionMode { get; set; } = SupervisionMode.None;
    public string FileRuleJson { get; set; } = "{}";
    public ExamStatus Status { get; set; } = ExamStatus.Draft;
    public int Version { get; set; } = 1;
    public Guid? CreatedBy { get; set; }
    public ICollection<ExamFile> Files { get; set; } = new List<ExamFile>();
    public ICollection<QuizQuestion> QuizQuestions { get; set; } = new List<QuizQuestion>();
    public ICollection<QuizImportSource> QuizImportSources { get; set; } = new List<QuizImportSource>();
}

public sealed class QuizQuestion : EntityBase
{
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public int Version { get; set; }
    public int Order { get; set; }
    public string Text { get; set; } = string.Empty;
    public decimal Points { get; set; }
    public bool Multiple { get; set; }
    public ICollection<QuizChoice> Choices { get; set; } = new List<QuizChoice>();
}

public sealed class QuizChoice : EntityBase
{
    public Guid QuestionId { get; set; }
    public QuizQuestion Question { get; set; } = null!;
    public int Order { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsCorrect { get; set; }
}

public sealed class QuizAttempt : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid SessionId { get; set; }
    public ExamSession Session { get; set; } = null!;
    public Guid ParticipantId { get; set; }
    public SessionParticipant Participant { get; set; } = null!;
    public int AttemptNumber { get; set; } = 1;
    public int ExamVersion { get; set; }
    public QuizAttemptStatus Status { get; set; } = QuizAttemptStatus.InProgress;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset? FinalizedAtUtc { get; set; }
    public decimal? AutoScore { get; set; }
    public decimal? Score { get; set; }
    public decimal MaxScore { get; set; }
    public GradingStatus GradingStatus { get; set; } = GradingStatus.InProgress;
    public string? GeneralComment { get; set; }
    public Guid? GraderId { get; set; }
    public DateTimeOffset? GradedAtUtc { get; set; }
    public DateTimeOffset? ReturnedAtUtc { get; set; }
    public QuizResultPolicy ResultPolicySnapshot { get; set; } = QuizResultPolicy.Hidden;
    public string SnapshotJson { get; set; } = "{}";
    public string? FinalizeIdempotencyKey { get; set; }
    public ICollection<QuizAnswer> Answers { get; set; } = new List<QuizAnswer>();
}

public sealed class QuizAnswer : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid AttemptId { get; set; }
    public QuizAttempt Attempt { get; set; } = null!;
    public Guid QuestionId { get; set; }
    public QuizQuestion Question { get; set; } = null!;
    public string ChoiceIdsJson { get; set; } = "[]";
    public long Revision { get; set; }
    public DateTimeOffset ClientUpdatedAtUtc { get; set; }
}

public sealed class QuizGradeMutationReceipt : EntityBase
{
    public Guid AttemptId { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public Guid? EventId { get; set; }
    public string AttemptRowVersion { get; set; } = string.Empty;
}

public sealed class ExamFile : EntityBase
{
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public int Version { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string StoredName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string TemporaryPath { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int ChunkSizeBytes { get; set; }
    public int TotalChunks { get; set; }
    public string ReceivedChunksJson { get; set; } = "[]";
    public TransferStatus TransferStatus { get; set; } = TransferStatus.Queued;
    public SyncStatus SyncStatus { get; set; } = SyncStatus.LocalOnly;
    public string? CloudObjectPath { get; set; }
}

public sealed class QuizImportSource : EntityBase
{
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public int ExamVersion { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Status { get; set; } = "Committed";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset ImportedAtUtc { get; set; }
}

public sealed class QuizImportPreview : EntityBase
{
    public string TokenHash { get; set; } = string.Empty;
    public Guid ExamId { get; set; }
    public int ExamVersion { get; set; }
    public Guid TeacherId { get; set; }
    public string ExamRowVersion { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string TemporaryPath { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = "{}";
    public string WarningsJson { get; set; } = "[]";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
}

public sealed class ExamSession : EntityBase
{
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public Guid? ClassId { get; set; }
    public string RoomCode { get; set; } = string.Empty;
    public SessionStatus Status { get; set; } = SessionStatus.Draft;
    public string HostDeviceId { get; set; } = string.Empty;
    public DateTimeOffset? PlannedStartUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public ExamDeliveryType DeliveryTypeSnapshot { get; set; } = ExamDeliveryType.FileSubmission;
    public SupervisionMode SupervisionModeSnapshot { get; set; } = SupervisionMode.None;
    public QuizResultPolicy QuizResultPolicySnapshot { get; set; } = QuizResultPolicy.Hidden;
    public int ExamVersionSnapshot { get; set; } = 1;
    public string SettingsJson { get; set; } = "{}";
    public bool AutoApprove { get; set; }
    public SessionAccessMode AccessMode { get; set; } = SessionAccessMode.LanOnly;
    public SessionAdmissionMode AdmissionMode { get; set; } = SessionAdmissionMode.ClassMembersOnly;
    public int? Capacity { get; set; }
    public bool AcceptingParticipants { get; set; } = true;
    public long Sequence { get; set; }
    public ICollection<SessionParticipant> Participants { get; set; } = new List<SessionParticipant>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public void TransitionTo(SessionStatus next)
    {
        SessionStateMachine.EnsureTransition(Status, next);
        Status = next;
        Sequence++;
        if (next == SessionStatus.InProgress && StartedAtUtc is null) StartedAtUtc = DateTimeOffset.UtcNow;
        if (next is SessionStatus.Finished or SessionStatus.Cancelled) EndedAtUtc = DateTimeOffset.UtcNow;
        if (next is SessionStatus.Collecting or SessionStatus.Finished or SessionStatus.Cancelled) AcceptingParticipants = false;
    }

    public DateTimeOffset? BaseDeadlineUtc(int durationMinutes) => StartedAtUtc?.AddMinutes(durationMinutes);
}

public sealed class SessionParticipant : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid SessionId { get; set; }
    public ExamSession Session { get; set; } = null!;
    public Guid? UserId { get; set; }
    public string StudentCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ClassName { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public ParticipantStatus Status { get; set; } = ParticipantStatus.PendingApproval;
    public DateTimeOffset JoinedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DownloadStatus DownloadStatus { get; set; } = DownloadStatus.NotStarted;
    public SubmissionStatus SubmissionStatus { get; set; } = SubmissionStatus.NotStarted;
    public int ExtraTimeMinutes { get; set; }
    public bool ResubmitAllowed { get; set; }
    public string? ResubmitReason { get; set; }
    public string? CapabilityJson { get; set; }
    public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}

public sealed class ParticipantExtraTime : EntityBase
{
    public Guid ParticipantId { get; set; }
    public SessionParticipant Participant { get; set; } = null!;
    public int Minutes { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
}

public sealed class Message : EntityBase
{
    public Guid SessionId { get; set; }
    public ExamSession Session { get; set; } = null!;
    public Guid? SenderId { get; set; }
    public Guid? ReceiverId { get; set; }
    public MessageType Type { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset? ReadAtUtc { get; set; }
}

public sealed class Submission : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid SessionId { get; set; }
    public ExamSession Session { get; set; } = null!;
    public Guid ParticipantId { get; set; }
    public SessionParticipant Participant { get; set; } = null!;
    public int AttemptNumber { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Preparing;
    public DateTimeOffset ClientSubmittedAtUtc { get; set; }
    public DateTimeOffset? ServerReceivedAtUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public bool IsLate { get; set; }
    public bool IsOfficial { get; set; }
    public string? ReceiptCode { get; set; }
    public string? ReceiptSignature { get; set; }
    public string? TeacherRejectReason { get; set; }
    public string? ClientNote { get; set; }
    public ICollection<SubmissionFile> Files { get; set; } = new List<SubmissionFile>();

    public void TransitionTo(SubmissionStatus next)
    {
        SubmissionStateMachine.EnsureTransition(Status, next);
        Status = next;
    }
}

public sealed class SubmissionFile : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid SubmissionId { get; set; }
    public Submission Submission { get; set; } = null!;
    public string ClientFileId { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string StoredName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string TemporaryPath { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int ChunkSizeBytes { get; set; }
    public int TotalChunks { get; set; }
    public string ReceivedChunksJson { get; set; } = "[]";
    public TransferStatus TransferStatus { get; set; } = TransferStatus.Queued;
    public SyncStatus SyncStatus { get; set; } = SyncStatus.LocalOnly;
    public string? CloudObjectPath { get; set; }
}

public sealed class Grade : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid SubmissionId { get; set; }
    public Submission Submission { get; set; } = null!;
    public GradingStatus Status { get; set; } = GradingStatus.NotGraded;
    public decimal? Score { get; set; }
    public decimal MaxScore { get; set; } = 10;
    public string? GeneralComment { get; set; }
    public Guid? GraderId { get; set; }
    public DateTimeOffset? GradedAtUtc { get; set; }
    public DateTimeOffset? ReturnedAtUtc { get; set; }
    public long Revision { get; set; }
    public ICollection<RubricScore> RubricScores { get; set; } = new List<RubricScore>();
    public ICollection<GradedAttachment> Attachments { get; set; } = new List<GradedAttachment>();
}

public sealed class EssayGradeMutationReceipt : EntityBase
{
    public Guid SubmissionId { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public Guid? EventId { get; set; }
    public long GradeRevision { get; set; }
}

public sealed class RubricScore : EntityBase
{
    public Guid GradeId { get; set; }
    public Grade Grade { get; set; } = null!;
    public string CriterionKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal Score { get; set; }
    public decimal MaxScore { get; set; }
    public string? Comment { get; set; }
    public int Order { get; set; }
}

public sealed class GradedAttachment : EntityBase
{
    public Guid GradeId { get; set; }
    public Grade Grade { get; set; } = null!;
    public string OriginalName { get; set; } = string.Empty;
    public string StoredName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public SyncStatus SyncStatus { get; set; } = SyncStatus.LocalOnly;
    public string? CloudObjectPath { get; set; }
}

public sealed class ControlPolicy : EntityBase
{
    public Guid SessionId { get; set; }
    public int Version { get; set; }
    public string PolicyJson { get; set; } = "{}";
    public PolicyApplyStatus Status { get; set; } = PolicyApplyStatus.NotRequested;
}

public sealed class DevicePolicyStatus : EntityBase
{
    public Guid SessionId { get; set; }
    public Guid ParticipantId { get; set; }
    public int PolicyVersion { get; set; }
    public string CapabilityJson { get; set; } = "{}";
    public PolicyApplyStatus Status { get; set; } = PolicyApplyStatus.NotRequested;
    public string? Error { get; set; }
}

public sealed class PublicDeviceConnection : EntityBase
{
    public string SourceMode { get; set; } = "PublicCloud";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "Pulled";
    public Guid SessionId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid UserId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public ConnectionState ConnectionState { get; set; }
    public DateTimeOffset HeartbeatAtUtc { get; set; }
    public string? ForegroundApplication { get; set; }
    public string? RunningProcessSummaryJson { get; set; }
    public string? PolicyState { get; set; }
    public string? LockState { get; set; }
    public int ViolationCount { get; set; }
    public string? AppVersion { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset? PolicyLeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? LastPolicyRenewalAtUtc { get; set; }
}

public sealed class PublicDeviceCommand : EntityBase
{
    public string SourceMode { get; set; } = "PublicCloud";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "Pulled";
    public Guid SessionId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DeviceCommandType CommandType { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid IssuedBy { get; set; }
    public string Signature { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTimeOffset? LastRetryAtUtc { get; set; }
}

public sealed class PublicDeviceCommandResult : EntityBase
{
    public string SourceMode { get; set; } = "PublicCloud";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "Pulled";
    public string DeviceId { get; set; } = string.Empty;
    public DeviceCommandStatus Status { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ExecutedAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class Violation : EntityBase
{
    public string SourceMode { get; set; } = "Lan";
    public long CloudVersion { get; set; }
    public DateTimeOffset? CloudUpdatedAtUtc { get; set; }
    public string CloudSyncState { get; set; } = "LocalOnly";
    public Guid SessionId { get; set; }
    public Guid ParticipantId { get; set; }
    public string Type { get; set; } = string.Empty;
    public ViolationSeverity Severity { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset? HandledAtUtc { get; set; }
    public Guid? HandledBy { get; set; }
}

public sealed class AuditLog : EntityBase
{
    public Guid? SessionId { get; set; }
    public string? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string TraceId { get; set; } = string.Empty;
}

public sealed class ExportJob : EntityBase
{
    public Guid SessionId { get; set; }
    public string OptionsJson { get; set; } = "{}";
    public ExportStatus Status { get; set; } = ExportStatus.Queued;
    public double Progress { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public SyncStatus SyncStatus { get; set; } = SyncStatus.LocalOnly;
    public string? CloudObjectPath { get; set; }
}

public sealed class BackupRecord : EntityBase
{
    public string RelativePath { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "1";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
    public BackupStatus Status { get; set; } = BackupStatus.Creating;
    public string? Error { get; set; }
    public SyncStatus SyncStatus { get; set; } = SyncStatus.LocalOnly;
    public string? CloudObjectPath { get; set; }
}

public sealed class SyncQueueItem : EntityBase
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Operation { get; set; } = "upsert";
    public string PayloadJson { get; set; } = "{}";
    public string? FilePath { get; set; }
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? CloudObjectPath { get; set; }
    public string? UploadUrl { get; set; }
    public long UploadOffset { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class PublicCloudPullCursor : EntityBase
{
    public string EntityName { get; set; } = string.Empty;
    public long LastCloudVersion { get; set; }
    public DateTimeOffset? LastUpdatedAtUtc { get; set; }
    public string? LastEntityId { get; set; }
}

public sealed class PublicCloudReplicaRecord : EntityBase
{
    public string EntityName { get; set; } = string.Empty;
    public string CloudEntityId { get; set; } = string.Empty;
    public long CloudVersion { get; set; }
    public DateTimeOffset CloudUpdatedAtUtc { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class PublicCloudIdMapping : EntityBase
{
    public string EntityName { get; set; } = string.Empty;
    public string CloudEntityId { get; set; } = string.Empty;
    public Guid LocalEntityId { get; set; }
}

public sealed class PublicCloudPullFailure : EntityBase
{
    public string EntityName { get; set; } = string.Empty;
    public string? CloudEntityId { get; set; }
    public string ErrorClass { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string RowVersion { get; set; } = Guid.NewGuid().ToString("N");
}
