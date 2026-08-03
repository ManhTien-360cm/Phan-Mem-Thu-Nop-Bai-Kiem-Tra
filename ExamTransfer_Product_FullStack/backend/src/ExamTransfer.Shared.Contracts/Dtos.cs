namespace ExamTransfer.Shared.Contracts;

public sealed record SystemStatusDto(
    bool Ready,
    string Version,
    DateTimeOffset ServerNowUtc,
    string DatabaseStatus,
    string StorageStatus,
    long DiskFreeBytes,
    string DiscoveryStatus,
    string CloudStatus,
    IReadOnlyList<string> Warnings);

public sealed record DashboardSummaryDto(
    int ClassCount,
    int ExamCount,
    int ActiveSessionCount,
    int PendingGradingCount,
    long StorageBytes,
    IReadOnlyList<SessionSummaryDto> RecentSessions,
    IReadOnlyList<string> Warnings,
    SessionSummaryDto? ActiveSession = null);

public sealed record ClassSummaryDto(Guid Id, string Name, string Code, string SchoolYear, ClassStatus Status, int StudentCount, string RowVersion, ClassAccessMode AccessMode = ClassAccessMode.Private, bool EnrollmentOpen = false);
public sealed record ClassDetailDto(Guid Id, string Name, string Code, string SchoolYear, string? Description, ClassStatus Status, IReadOnlyList<StudentDto> Students, string RowVersion, ClassAccessMode AccessMode = ClassAccessMode.Private, bool EnrollmentOpen = false, bool RequireEnrollmentApproval = true, IReadOnlyList<ClassEnrollmentRequestDto>? EnrollmentRequests = null);
public sealed record StudentDto(Guid Id, string StudentCode, string DisplayName, string? Email, string? MetadataJson);
public sealed record ClassEnrollmentRequestDto(
    Guid Id,
    Guid ClassId,
    Guid StudentUserId,
    string StudentCode,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    long CloudVersion);

public sealed record ExamSummaryDto(Guid Id, Guid? ClassId, string Title, string Subject, int DurationMinutes, ExamDeliveryType DeliveryType, ExamStatus Status, int Version, int FileCount, string RowVersion, QuizResultPolicy QuizResultPolicy = QuizResultPolicy.Hidden, SupervisionMode SupervisionMode = SupervisionMode.None, bool HasCommittedQuizSource = false, int QuizQuestionCount = 0);
public sealed record ExamDetailDto(Guid Id, Guid? ClassId, string Title, string Subject, string? Description, int DurationMinutes, ExamDeliveryType DeliveryType, ExamStatus Status, int Version, FileRuleDto FileRule, IReadOnlyList<FileDescriptorDto> Files, string RowVersion, QuizResultPolicy QuizResultPolicy = QuizResultPolicy.Hidden, SupervisionMode SupervisionMode = SupervisionMode.None, QuizImportSourceDto? QuizSource = null, int QuizQuestionCount = 0);
public sealed record ExamManifestDto(Guid ExamId, int Version, DateTimeOffset GeneratedAtUtc, IReadOnlyList<FileDescriptorDto> Files);

public sealed record SessionCountsDto(int Total, int Pending, int Approved, int Connected, int Submitted, int Uploading, int Disconnected);
public sealed record SessionSummaryDto(Guid Id, Guid ExamId, string Title, string RoomCode, SessionStatus Status, DateTimeOffset ServerNowUtc, DateTimeOffset? StartTimeUtc, DateTimeOffset? EndTimeUtc, DateTimeOffset? EffectiveDeadlineUtc, SessionCountsDto Counts, long Sequence, string RowVersion, SessionAccessMode AccessMode = SessionAccessMode.LanOnly, bool AutoApprove = false, ExamDeliveryType DeliveryType = ExamDeliveryType.FileSubmission, SupervisionMode SupervisionMode = SupervisionMode.None, QuizResultPolicy QuizResultPolicy = QuizResultPolicy.Hidden, int ExamVersion = 1, SessionAdmissionMode AdmissionMode = SessionAdmissionMode.ClassMembersOnly);
public sealed record SessionDetailDto(SessionSummaryDto Summary, IReadOnlyList<ParticipantDto> Participants, string SettingsJson, DateTimeOffset? PlannedStartUtc = null, int? Capacity = null);

public sealed record OpenSessionDiscoveryDto(
    Guid SessionId,
    string RoomCode,
    string RoomName,
    Guid? ClassId,
    string? ClassCode,
    string? ClassName,
    string ExamTitle,
    string TeacherName,
    SessionStatus SessionState,
    bool RequireApproval,
    int? Capacity,
    int CurrentParticipantCount,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ScheduledStartUtc,
    SessionAccessMode AccessMode,
    string ServerId,
    string ServerName,
    string BaseAddress,
    DateTimeOffset RespondedAtUtc,
    string ProtocolVersion,
    string Subject = "",
    int DurationMinutes = 0,
    ExamDeliveryType DeliveryType = ExamDeliveryType.FileSubmission,
    SupervisionMode SupervisionMode = SupervisionMode.None,
    SessionAdmissionMode AdmissionMode = SessionAdmissionMode.ClassMembersOnly,
    Guid? ExamId = null);

public sealed record ParticipantDto(
    Guid Id,
    Guid SessionId,
    string StudentCode,
    string DisplayName,
    string DeviceId,
    string MachineName,
    string? IpAddress,
    string AppVersion,
    ParticipantStatus Status,
    DateTimeOffset? LastSeenUtc,
    DownloadStatus DownloadStatus,
    SubmissionStatus SubmissionStatus,
    int ExtraTimeMinutes,
    DateTimeOffset? EffectiveDeadlineUtc,
    ConnectionState ConnectionState,
    bool ResubmitAllowed = false);

public sealed record SubmissionFileDto(Guid Id, string Name, long SizeBytes, string Sha256, string MimeType, int TotalChunks, IReadOnlyList<int> ReceivedChunks, TransferStatus TransferStatus, string? DownloadUrl);
public sealed record SubmissionSummaryDto(Guid Id, Guid SessionId, Guid ParticipantId, string StudentCode, string DisplayName, int AttemptNumber, SubmissionStatus Status, DateTimeOffset? ClientSubmittedAtUtc, DateTimeOffset? ServerReceivedAtUtc, DateTimeOffset DeadlineUtc, bool IsLate, string? ReceiptCode, bool IsOfficial, IReadOnlyList<SubmissionFileDto> Files);
public sealed record ReceiptDto(Guid SubmissionId, string ReceiptCode, string Signature, DateTimeOffset ServerReceivedAtUtc, bool IsLate, IReadOnlyList<FileDescriptorDto> Files);

public sealed record GradeDto(Guid SubmissionId, GradingStatus Status, decimal? Score, decimal MaxScore, IReadOnlyList<RubricScoreDto> RubricScores, string? GeneralComment, IReadOnlyList<FileDescriptorDto> Attachments, DateTimeOffset? ReturnedAtUtc, string RowVersion)
{
    public Guid? GradeId { get; init; }
    public long Revision { get; init; }
}
public sealed record RubricScoreDto(string CriterionKey, string Title, decimal Score, decimal MaxScore, string? Comment, int Order);

public sealed record ControlCapabilitiesDto(bool Fullscreen, bool FocusMonitoring, bool ClipboardControl, bool ProcessControl, bool NetworkControl);
public sealed record ControlPolicyDto(Guid SessionId, int Version, bool Fullscreen, string FocusRule, string ClipboardRule, IReadOnlyList<string> AllowedProcesses, IReadOnlyList<string> BlockedProcesses, string NetworkRule, bool EmergencyExit, int TtlMinutes, string RowVersion);
public sealed record DeviceControlStatusDto(
    Guid ParticipantId,
    int PolicyVersion,
    ControlCapabilitiesDto Capabilities,
    PolicyApplyStatus Status,
    string? Error,
    DateTimeOffset UpdatedAtUtc,
    string? DeviceId = null,
    ConnectionState ConnectionState = ConnectionState.Offline,
    DateTimeOffset? HeartbeatAtUtc = null,
    string? PolicyState = null,
    string? LockState = null,
    string? AppVersion = null,
    string? AgentVersion = null,
    DeviceCommandStatus? LastCommandStatus = null,
    string? LastCommandError = null);
public sealed record ViolationDto(Guid Id, Guid SessionId, Guid ParticipantId, string Type, ViolationSeverity Severity, DateTimeOffset OccurredAtUtc, string? PayloadJson, DateTimeOffset? HandledAtUtc, Guid? HandledBy);

public sealed record DeviceCommandDto(
    Guid CommandId,
    Guid SessionId,
    string DeviceId,
    DeviceCommandType CommandType,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    Guid IssuedBy,
    string Signature);

public sealed record DeviceCommandResultDto(
    Guid CommandId,
    string DeviceId,
    DeviceCommandStatus Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ExecutedAtUtc,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ExportJobDto(Guid Id, Guid SessionId, ExportStatus Status, double Progress, string? OutputFileName, string? Error, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc);
public sealed record BackupDto(Guid Id, string FileName, long SizeBytes, string Sha256, string SchemaVersion, bool Encrypted, BackupStatus Status, DateTimeOffset CreatedAtUtc);
public sealed record CloudSyncStatusDto(
    bool Enabled,
    SyncStatus Status,
    int PendingItems,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool Configured = false,
    string? OrganizationId = null,
    string UploadStrategy = "LocalOnly",
    string AccessMode = CloudAccessModes.UserSession,
    bool Authenticated = false,
    bool CanSynchronize = false);
public sealed record AuditLogDto(Guid Id, Guid? SessionId, string? ActorId, string Action, string EntityType, string? EntityId, string? IpAddress, string? BeforeJson, string? AfterJson, string TraceId, DateTimeOffset CreatedAtUtc);
public sealed record MessageDto(Guid Id, Guid SessionId, Guid? SenderId, Guid? ReceiverId, MessageType Type, string Content, DateTimeOffset CreatedAtUtc);
