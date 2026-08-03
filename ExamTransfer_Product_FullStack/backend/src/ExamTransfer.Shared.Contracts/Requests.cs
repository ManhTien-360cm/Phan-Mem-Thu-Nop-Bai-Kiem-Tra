namespace ExamTransfer.Shared.Contracts;

public sealed record SetModeRequest(string Mode, bool Remember);
public sealed record LoginRequest(string Email, string Password);
public sealed record AccountLoginRequest(string Account, string Password, string DeviceId, string MachineName, string AppVersion);
public sealed record StudentIdentityConfirmRequest(string ChallengeToken, string StudentCode, string DisplayName, string DeviceId, string MachineName);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);
public sealed record AccountHeartbeatRequest(string DeviceId, string MachineName, DateTimeOffset ClientNowUtc);
public sealed record AccountHeartbeatResponse(bool Active, DateTimeOffset ServerNowUtc, DateTimeOffset LeaseExpiresAtUtc, int NextHeartbeatSeconds);
public sealed record LogoutRequest(string? DeviceId = null, string? Reason = null);

public sealed record CreateClassRequest(string Name, string Code, string SchoolYear, string? Description, ClassAccessMode AccessMode = ClassAccessMode.Private);
public sealed record UpdateClassRequest(string Name, string Code, string SchoolYear, string? Description, string RowVersion, ClassAccessMode? AccessMode = null);
public sealed record CreateStudentRequest(string StudentCode, string DisplayName, string? Email, string? MetadataJson);
public sealed record UpdateStudentRequest(string StudentCode, string DisplayName, string? Email, string? MetadataJson);
public sealed record ImportPreviewRequest(string FileName, string ContentBase64, IReadOnlyDictionary<string, string>? ColumnMapping);
public sealed record ImportCommitRequest(string PreviewToken, bool SkipInvalidRows);
public sealed record ImportRowErrorDto(int RowNumber, string Field, string Code, string Message);
public sealed record ImportPreviewDto(string PreviewToken, int TotalRows, int ValidRows, int InvalidRows, IReadOnlyList<StudentDto> PreviewStudents, IReadOnlyList<ImportRowErrorDto> Errors);
public sealed record ImportCommitResultDto(int Inserted, int Skipped, IReadOnlyList<ImportRowErrorDto> Errors);
public sealed record BulkArchiveRequest(IReadOnlyList<Guid> Ids);
public sealed record BulkArchiveFailureDto(Guid Id, string Code, string Message);
public sealed record BulkArchiveResultDto(
    int Requested,
    int Archived,
    IReadOnlyList<Guid> AlreadyArchived,
    IReadOnlyList<BulkArchiveFailureDto> Rejected);

public sealed record CreateExamRequest(Guid? ClassId, string Title, string Subject, string? Description, int DurationMinutes, FileRuleDto FileRule, ExamDeliveryType DeliveryType = ExamDeliveryType.FileSubmission, QuizResultPolicy QuizResultPolicy = QuizResultPolicy.Hidden, SupervisionMode SupervisionMode = SupervisionMode.None);
public sealed record UpdateExamRequest(Guid? ClassId, string Title, string Subject, string? Description, int DurationMinutes, FileRuleDto FileRule, string RowVersion, ExamDeliveryType DeliveryType = ExamDeliveryType.FileSubmission, QuizResultPolicy QuizResultPolicy = QuizResultPolicy.Hidden, SupervisionMode SupervisionMode = SupervisionMode.None);
public sealed record InitFileUploadRequest(string FileName, long SizeBytes, string Sha256, string MimeType, int? ChunkSizeBytes = null);
public sealed record InitFileUploadResponse(Guid FileId, int ChunkSizeBytes, int TotalChunks, IReadOnlyList<int> MissingChunks);
public sealed record FinalizeFileUploadRequest(string Sha256);

public sealed record CreateSessionRequest(
    Guid ExamId,
    Guid? ClassId,
    DateTimeOffset? PlannedStartUtc,
    string SettingsJson,
    bool AutoApprove,
    int? Capacity,
    string? CustomRoomCode,
    SessionAccessMode AccessMode = SessionAccessMode.LanOnly,
    SessionAdmissionMode AdmissionMode = SessionAdmissionMode.ClassMembersOnly);
public sealed record UpdateSessionRequest(DateTimeOffset? PlannedStartUtc, string SettingsJson, bool AutoApprove, int? Capacity, string RowVersion, bool ApprovePendingParticipants = false);
public sealed record ChangePublicCloudRoomCodeRequest(string? NewRoomCode, string RowVersion);
public sealed record JoinSessionRequest(string RoomCode, string StudentCode, string DisplayName, string? ClassName, string DeviceId, string MachineName, string AppVersion, string Nonce);
public sealed record JoinSessionResponse(Guid SessionId, Guid ParticipantId, ParticipantStatus Status, string AccessToken, DateTimeOffset TokenExpiresAtUtc, ParticipantDto Participant);
public sealed record TeacherMutationRequest(Guid MutationRequestId);
public sealed record ReasonedTeacherMutationRequest(string? Reason, Guid MutationRequestId);
public sealed record ExtraTimeRequest(int Minutes, string Reason, Guid MutationRequestId);
public sealed record SendMessageRequest(Guid? ReceiverParticipantId, MessageType Type, string Content);
public sealed record EndSessionRequest(bool Force, string? Reason);
public sealed record BulkApproveRequest(IReadOnlyList<Guid> ParticipantIds, Guid MutationRequestId);
public sealed record HeartbeatRequest(string DeviceStatus, DateTimeOffset ClientNowUtc, long SequenceAck);
public sealed record HeartbeatResponse(DateTimeOffset ServerNowUtc);

public sealed record InitSubmissionFileRequest(string ClientFileId, string Name, long SizeBytes, string Sha256, string MimeType);
public sealed record InitSubmissionRequest(Guid SessionId, Guid ParticipantId, string IdempotencyKey, IReadOnlyList<InitSubmissionFileRequest> Files, DateTimeOffset ClientSubmittedAtUtc);
public sealed record InitSubmissionResponse(Guid SubmissionId, int AttemptNumber, int ChunkSizeBytes, IReadOnlyList<ChunkPlanDto> FilePlans, DateTimeOffset DeadlineUtc);
public sealed record FinalizeSubmissionRequest(string? ClientNote);
public sealed record FinalizeSubmissionResponse(SubmissionStatus Status, DateTimeOffset ServerReceivedAtUtc, bool IsLate, string ReceiptCode, string ReceiptSignature, IReadOnlyList<FileDescriptorDto> Files);
public sealed record RejectSubmissionRequest(string Reason, Guid MutationRequestId);
public sealed record AllowResubmitRequest(string Reason, Guid MutationRequestId);

public sealed record CreateExportRequest(Guid SessionId, bool IncludeFiles, bool IncludeManifest, bool IncludeReceipts, bool IncludeAudit, string Format, string NamingPattern);
public sealed record CreateBackupRequest(bool IncludeFiles, bool Encrypt, string? PasswordHint);
public sealed record RestoreBackupRequest(string ConfirmationText);
public sealed record RestoreScheduledDto(Guid BackupId, bool RequiresRestart, string Message);

public sealed record SaveGradeRequest(
    decimal? Score,
    decimal MaxScore,
    IReadOnlyList<RubricScoreDto> RubricScores,
    string? GeneralComment,
    string RowVersion,
    Guid MutationRequestId = default);
public sealed record ReturnGradeRequest(
    string? Message,
    string RowVersion = "",
    Guid MutationRequestId = default);
public sealed record ReopenGradeRequest(
    string Reason,
    string RowVersion = "",
    Guid MutationRequestId = default);

public sealed record SaveControlPolicyRequest(bool Fullscreen, string FocusRule, string ClipboardRule, IReadOnlyList<string> AllowedProcesses, IReadOnlyList<string> BlockedProcesses, string NetworkRule, bool EmergencyExit, int TtlMinutes, string? RowVersion);
public sealed record ApplyControlPolicyRequest(IReadOnlyList<Guid>? ParticipantIds);
public sealed record ControlActionRequest(ControlActionType Action, string Reason);
public sealed record ViolationReportRequest(string Type, ViolationSeverity Severity, DateTimeOffset OccurredAtUtc, string? PayloadJson);
public sealed record PolicyApplyAckRequest(int PolicyVersion, PolicyApplyStatus Status, IReadOnlyList<string> UnsupportedRules, string? Error, ControlCapabilitiesDto Capabilities);
public sealed record ClientReadyRequest(string AgentVersion, string OsVersion, ControlCapabilitiesDto Capabilities);

public sealed record UpdateSettingsRequest(
    int ServerPort,
    bool UseHttps,
    bool DiscoveryEnabled,
    int DiscoveryPort,
    string StorageRootPath,
    long MinFreeBytes,
    int ChunkSizeBytes,
    int MaxConcurrentUploads,
    int HeartbeatSeconds,
    int DisconnectAfterSeconds,
    bool CloudEnabled,
    int TemporaryHours,
    int LogsDays,
    string RowVersion,
    string? SupabaseUrl = null,
    string? SupabasePublishableKey = null,
    string? OrganizationId = null,
    string CloudEnvironment = "Development",
    bool CloudUseResumableUploads = true,
    string CloudAccessMode = CloudAccessModes.UserSession);

public sealed record SettingsDto(
    int ServerPort,
    bool UseHttps,
    bool DiscoveryEnabled,
    int DiscoveryPort,
    string StorageRootPath,
    long MinFreeBytes,
    int ChunkSizeBytes,
    int MaxConcurrentUploads,
    int HeartbeatSeconds,
    int DisconnectAfterSeconds,
    bool CloudEnabled,
    int TemporaryHours,
    int LogsDays,
    string RowVersion,
    string? SupabaseUrl = null,
    string? SupabasePublishableKey = null,
    string? OrganizationId = null,
    string CloudEnvironment = "Development",
    bool CloudUseResumableUploads = true,
    bool CloudSecretConfigured = false,
    string CloudConfigurationStatus = "NotConfigured",
    string CloudAccessMode = CloudAccessModes.UserSession,
    bool CloudAuthenticated = false,
    string? CloudAuthenticatedEmail = null);

public sealed record CloudPreflightDto(
    bool Enabled,
    bool Configured,
    bool Reachable,
    bool SecretConfigured,
    string KeyMode,
    string? OrganizationId,
    string UploadStrategy,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string AccessMode = CloudAccessModes.UserSession,
    bool Authenticated = false,
    string? AuthenticatedEmail = null,
    bool CanSynchronize = false);

public sealed record CloudSessionDto(
    bool Authenticated,
    string? UserId,
    string? Email,
    DateTimeOffset? ExpiresAtUtc,
    string? OrganizationId,
    string? Role);
