using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Application;

public interface IAppDbContext
{
    IQueryable<User> Users { get; }
    IQueryable<UserLoginSession> UserLoginSessions { get; }
    IQueryable<ClassRoom> Classes { get; }
    IQueryable<ClassMember> ClassMembers { get; }
    IQueryable<Exam> Exams { get; }
    IQueryable<ExamFile> ExamFiles { get; }
    IQueryable<ExamSession> ExamSessions { get; }
    IQueryable<SessionParticipant> SessionParticipants { get; }
    IQueryable<ParticipantExtraTime> ParticipantExtraTimes { get; }
    IQueryable<Message> Messages { get; }
    IQueryable<Submission> Submissions { get; }
    IQueryable<SubmissionFile> SubmissionFiles { get; }
    IQueryable<Grade> Grades { get; }
    IQueryable<RubricScore> RubricScores { get; }
    IQueryable<GradedAttachment> GradedAttachments { get; }
    IQueryable<ControlPolicy> ControlPolicies { get; }
    IQueryable<DevicePolicyStatus> DevicePolicyStatuses { get; }
    IQueryable<Violation> Violations { get; }
    IQueryable<AuditLog> AuditLogs { get; }
    IQueryable<ExportJob> ExportJobs { get; }
    IQueryable<BackupRecord> Backups { get; }
    IQueryable<SyncQueueItem> SyncQueue { get; }
    IQueryable<AppSetting> AppSettings { get; }

    void Add<TEntity>(TEntity entity) where TEntity : class;
    void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<IAppTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

public interface IAppTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IStoragePaths
{
    string RootPath { get; }
    string DatabasePath { get; }
    string BackupRoot { get; }
    string ExportRoot { get; }
    string TemporaryRoot { get; }
    string ExamVersionRoot(Guid examId, int version);
    string SessionRoot(Guid sessionId);
    string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId);
    string ReceiptRoot(Guid sessionId);
    void EnsureCreated();
}

public interface IChunkStorage
{
    Task WriteChunkAsync(string transferRoot, int index, Stream content, long maxBytes, string? expectedSha256, CancellationToken cancellationToken);
    Task<string> AssembleAndVerifyAsync(string transferRoot, int totalChunks, long expectedSize, string expectedSha256, string outputPath, CancellationToken cancellationToken);
    IReadOnlyList<int> ReadReceivedChunks(string json);
    string WriteReceivedChunks(IReadOnlyCollection<int> chunks);
    Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken);
}

public interface IReceiptSigner
{
    (string ReceiptCode, string Signature) Create(Guid submissionId, DateTimeOffset receivedAtUtc, IReadOnlyList<FileDescriptorDto> files);
    bool Verify(Guid submissionId, DateTimeOffset receivedAtUtc, IReadOnlyList<FileDescriptorDto> files, string signature);
}

public interface ISessionTokenService
{
    IssuedToken IssueParticipantToken(Guid sessionId, Guid participantId, Guid userId, string deviceId, ParticipantStatus status, TimeSpan lifetime);
    TokenPrincipal? Validate(string token);
}

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAtUtc);
public sealed record TokenPrincipal(Guid SessionId, Guid ParticipantId, Guid UserId, string DeviceId, DateTimeOffset ExpiresAtUtc, UserRole Role, ParticipantStatus ParticipantStatus);

public sealed record ExternalIdentityResult(
    string ProviderUserId,
    string Account,
    string? Email,
    string? RefreshToken,
    DateTimeOffset? ProviderExpiresAtUtc,
    ExternalApplicationProfile Profile,
    string? AccessToken = null);

public sealed record ExternalApplicationProfile(
    string ProviderUserId,
    string? OrganizationId,
    string? Username,
    string? DisplayName,
    string? StudentCode,
    string? Role,
    bool IsActive,
    DateOnly? DateOfBirth = null,
    bool MustChangePassword = false);

public interface IExternalIdentityProvider
{
    Task<ExternalIdentityResult> AuthenticateAsync(AccountLoginRequest request, CancellationToken cancellationToken);
}

public sealed record ExternalPasswordChangeRequest(
    string ProviderUserId,
    string Account,
    string CurrentPassword,
    string NewPassword);

public interface IExternalAccountSecurityService
{
    Task ChangePasswordAsync(ExternalPasswordChangeRequest request, CancellationToken cancellationToken);
}


public sealed record AccountTokenPrincipal(
    Guid UserId,
    Guid LoginSessionId,
    UserRole Role,
    string? OrganizationId,
    string DeviceId,
    DateTimeOffset ExpiresAtUtc);

public interface IAccountTokenService
{
    IssuedToken IssueAccountToken(Guid userId, Guid loginSessionId, UserRole role, string? organizationId, string deviceId, TimeSpan lifetime);
    AccountTokenPrincipal? ValidateAccountToken(string token);
    string HashToken(string token);
}

public sealed record LoginChallenge(Guid UserId, string DeviceId, string MachineName, DateTimeOffset ExpiresAtUtc);

public interface ILoginChallengeService
{
    IssuedToken IssueChallenge(Guid userId, string deviceId, string machineName, TimeSpan lifetime);
    LoginChallenge? ValidateAndConsume(string challengeToken, string deviceId);
}

public sealed record AccountSessionValidation(User User, UserLoginSession Session);

public interface IAccountSessionService
{
    Task<UserLoginSession> ClaimAsync(User user, string deviceId, string machineName, string? ipAddress, string? encryptedRefreshToken, CancellationToken cancellationToken);
    Task StoreTokenHashAsync(Guid loginSessionId, string tokenHash, CancellationToken cancellationToken);
    Task<AccountSessionValidation?> ValidateAsync(AccountTokenPrincipal principal, CancellationToken cancellationToken);
    Task<AccountHeartbeatResponse> HeartbeatAsync(AccountTokenPrincipal principal, string machineName, CancellationToken cancellationToken);
    Task LogoutAsync(AccountTokenPrincipal principal, string? reason, CancellationToken cancellationToken);
}

public interface IAccountAuthenticationService
{
    Task<AccountLoginResultDto> LoginAsync(AccountLoginRequest request, string? ipAddress, CancellationToken cancellationToken);
    Task<AccountLoginResultDto> ConfirmStudentAsync(StudentIdentityConfirmRequest request, string? ipAddress, CancellationToken cancellationToken);
    Task<CurrentAccountDto> GetCurrentAsync(AccountTokenPrincipal principal, CancellationToken cancellationToken);
    Task<PasswordChangeResultDto> ChangePasswordAsync(AccountTokenPrincipal principal, ChangePasswordRequest request, CancellationToken cancellationToken);
    Task<AccountHeartbeatResponse> HeartbeatAsync(AccountTokenPrincipal principal, AccountHeartbeatRequest request, CancellationToken cancellationToken);
    Task LogoutAsync(AccountTokenPrincipal principal, LogoutRequest request, CancellationToken cancellationToken);
}

public interface IRealtimePublisher
{
    Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default);
    Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default);
}

public interface IOnlyLanStudentNotificationTransport
{
    Task PublishSessionAsync(StudentNotificationEventDto notification, CancellationToken cancellationToken = default);
    Task PublishParticipantAsync(StudentNotificationEventDto notification, CancellationToken cancellationToken = default);
    Task PublishConnectionAsync(string connectionId, StudentNotificationEventDto notification, CancellationToken cancellationToken = default);
}

public interface ILanAccessPolicy
{
    bool IsAllowed(string? remoteAddress);

    LanAccessDecision Evaluate(string? remoteAddress)
    {
        var allowed = IsAllowed(remoteAddress);
        return new(
            allowed,
            "Unknown",
            remoteAddress,
            remoteAddress,
            [],
            null,
            allowed ? "ALLOWED" : "DENIED");
    }
}

public sealed record LanAccessDecision(
    bool Allowed,
    string RuntimeMode,
    string? RemoteIp,
    string? EffectiveClientIp,
    IReadOnlyList<string> AllowedCidrs,
    string? MatchedRange,
    string DeniedReason);

public interface IAuditService
{
    Task WriteAsync(string action, string entityType, string? entityId, Guid? sessionId, object? before, object? after, CancellationToken cancellationToken = default);
}

public interface IOutboxService
{
    Task EnqueueAsync(string entityType, string entityId, string operation, object payload, string? filePath = null, CancellationToken cancellationToken = default);
}

public interface ICloudSyncSignal
{
    void Pulse();
    Task<bool> WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken);
}

public sealed record CloudProjectionReadiness(
    Guid SessionId,
    bool Required,
    bool Ready,
    SyncStatus Status,
    string Code,
    string Message,
    int RetryCount);

public sealed record CloudSyncFailure(string Code, string Message);

public interface ICloudAdapter
{
    bool Enabled { get; }
    bool Configured { get; }
    bool Authenticated { get; }
    bool CanSynchronize { get; }
    CloudLoginResult? CurrentSession { get; }
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken);
    Task<CloudPushResult> PushAsync(
        SyncQueueItem item,
        Func<CancellationToken, Task>? checkpoint,
        CancellationToken cancellationToken);
    Task<CloudPullPage> PullAsync(
        string entityName,
        CloudPullCursorValue cursor,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CloudPullPage([], false));
    Task<CloudParticipantMutationResult> ApprovePublicParticipantAsync(
        Guid sessionId,
        Guid participantId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudParticipantMutationResult> RejectPublicParticipantAsync(
        Guid sessionId,
        Guid participantId,
        string? reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudBulkParticipantMutationResult> BulkApprovePublicParticipantsAsync(
        Guid sessionId,
        IReadOnlyList<Guid> participantIds,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudParticipantMutationResult> AddPublicParticipantExtraTimeAsync(
        Guid sessionId,
        Guid participantId,
        int minutes,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudParticipantMutationResult> AllowPublicResubmissionAsync(
        Guid participantId,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudSubmissionMutationResult> RejectPublicSubmissionAsync(
        Guid submissionId,
        string reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<MessageDto> SendPublicTeacherMessageAsync(
        Guid sessionId,
        Guid? participantId,
        MessageType messageType,
        string content,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher messages are not supported by this adapter.");
    Task<CloudEnrollmentMutationResult> ApprovePublicEnrollmentAsync(
        Guid enrollmentRequestId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudEnrollmentMutationResult> RejectPublicEnrollmentAsync(
        Guid enrollmentRequestId,
        string? reason,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud teacher mutations are not supported by this adapter.");
    Task<CloudQuizGradeMutationResult> SavePublicQuizGradeAsync(
        Guid attemptId,
        decimal? score,
        string? generalComment,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud quiz grading is not supported by this adapter.");
    Task<CloudQuizGradeMutationResult> ReturnPublicQuizGradeAsync(
        Guid attemptId,
        string? message,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud quiz grading is not supported by this adapter.");
    Task<CloudQuizGradeMutationResult> ReopenPublicQuizGradeAsync(
        Guid attemptId,
        string reason,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud quiz grading is not supported by this adapter.");
    Task<CloudEssayGradeResult> GetPublicEssayGradeAsync(
        Guid submissionId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud essay grading is not supported by this adapter.");
    Task<CloudEssayGradeResult> SavePublicEssayGradeAsync(
        Guid submissionId,
        decimal? score,
        IReadOnlyList<RubricScoreDto> rubricScores,
        string? generalComment,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud essay grading is not supported by this adapter.");
    Task<CloudEssayGradeResult> ReturnPublicEssayGradeAsync(
        Guid submissionId,
        string? message,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud essay grading is not supported by this adapter.");
    Task<CloudEssayGradeResult> ReopenPublicEssayGradeAsync(
        Guid submissionId,
        string reason,
        long expectedCloudVersion,
        Guid requestId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("PublicCloud essay grading is not supported by this adapter.");
    Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken);
    Task DownloadObjectToAsync(
        string cloudObjectPath,
        Stream destination,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Cloud object streaming is not supported by this adapter.");
    Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken);
}

public sealed record CloudLoginResult(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string UserId,
    string Email,
    string? OrganizationId = null,
    string? Role = null);

public sealed record CloudPushResult(
    bool Deleted,
    string? CloudObjectPath,
    string UploadStrategy,
    long BytesTransferred);

public sealed record CloudParticipantMutationResult(
    Guid ParticipantId,
    Guid SessionId,
    ParticipantStatus Status,
    DateTimeOffset? ApprovedAtUtc,
    int ExtraTimeMinutes,
    bool ResubmitAllowed,
    string? ResubmitReason,
    long CloudVersion,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? EffectiveDeadlineUtc = null,
    Guid? AttemptId = null,
    string? AttemptStatus = null,
    DateTimeOffset? AttemptDeadlineUtc = null,
    long? AttemptRevision = null,
    DateTimeOffset? ServerNowUtc = null,
    long? Revision = null,
    Guid? RequestId = null);

public sealed record CloudBulkParticipantMutationResult(
    int ApprovedCount,
    int SkippedCount,
    IReadOnlyList<CloudParticipantMutationResult> Participants);

public sealed record CloudSubmissionMutationResult(
    Guid SubmissionId,
    Guid SessionId,
    Guid ParticipantId,
    SubmissionStatus Status,
    string? TeacherRejectReason,
    long CloudVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record CloudEnrollmentMutationResult(
    Guid EnrollmentRequestId,
    Guid ClassId,
    string Status,
    DateTimeOffset? DecidedAtUtc,
    long CloudVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record CloudQuizGradeMutationResult(
    Guid AttemptId,
    Guid SessionId,
    Guid ParticipantId,
    decimal? AutoScore,
    decimal? Score,
    decimal MaxScore,
    GradingStatus Status,
    string? GeneralComment,
    Guid? GraderId,
    DateTimeOffset? GradedAtUtc,
    DateTimeOffset? ReturnedAtUtc,
    long CloudVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record CloudGradeAttachmentResult(
    Guid Id,
    string Name,
    long SizeBytes,
    string Sha256,
    string MimeType);

public sealed record CloudEssayGradeResult(
    Guid? GradeId,
    Guid SubmissionId,
    Guid SessionId,
    Guid ParticipantId,
    decimal? Score,
    decimal MaxScore,
    GradingStatus Status,
    string? GeneralComment,
    Guid? GraderId,
    DateTimeOffset? GradedAtUtc,
    DateTimeOffset? ReturnedAtUtc,
    long Revision,
    long CloudVersion,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<RubricScoreDto> RubricScores,
    IReadOnlyList<CloudGradeAttachmentResult> Attachments);

public sealed record CloudPreflightResult(
    bool Enabled,
    bool Configured,
    bool Reachable,
    bool SecretConfigured,
    string KeyMode,
    string? OrganizationId,
    string UploadStrategy,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string AccessMode,
    bool Authenticated,
    string? AuthenticatedEmail,
    bool CanSynchronize);

public sealed record CloudBackupDescriptor(
    Guid Id,
    string FileName,
    long SizeBytes,
    string Sha256,
    string SchemaVersion,
    bool Encrypted,
    string Status,
    string CloudObjectPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IBackupEngine
{
    Task CreateDatabaseSnapshotAsync(string destinationPath, CancellationToken cancellationToken);
}

public interface IClassService
{
    Task<PagedResult<ClassSummaryDto>> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<ClassDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ClassDetailDto> CreateAsync(CreateClassRequest request, CancellationToken cancellationToken);
    Task<ClassDetailDto> UpdateAsync(Guid id, UpdateClassRequest request, CancellationToken cancellationToken);
    Task ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<BulkArchiveResultDto> BulkArchiveAsync(BulkArchiveRequest request, CancellationToken cancellationToken);
    Task<StudentDto> AddStudentAsync(Guid classId, CreateStudentRequest request, CancellationToken cancellationToken);
    Task<StudentDto> UpdateStudentAsync(Guid classId, Guid studentId, UpdateStudentRequest request, CancellationToken cancellationToken);
    Task RemoveStudentAsync(Guid classId, Guid studentId, CancellationToken cancellationToken);
    Task<ImportPreviewDto> PreviewImportAsync(Guid classId, ImportPreviewRequest request, CancellationToken cancellationToken);
    Task<ImportCommitResultDto> CommitImportAsync(Guid classId, ImportCommitRequest request, CancellationToken cancellationToken);
    Task<byte[]> ExportCsvAsync(Guid classId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClassEnrollmentRequestDto>> ListEnrollmentRequestsAsync(Guid classId, CancellationToken cancellationToken);
    Task<ClassEnrollmentRequestDto> ApproveEnrollmentAsync(Guid classId, Guid requestId, Guid mutationRequestId, CancellationToken cancellationToken);
    Task<ClassEnrollmentRequestDto> RejectEnrollmentAsync(Guid classId, Guid requestId, string? reason, Guid mutationRequestId, CancellationToken cancellationToken);
}

public interface IExamService
{
    Task<PagedResult<ExamSummaryDto>> ListAsync(string? search, ExamStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<ExamDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ExamDetailDto> CreateAsync(CreateExamRequest request, CancellationToken cancellationToken);
    Task<ExamDetailDto> UpdateAsync(Guid id, UpdateExamRequest request, CancellationToken cancellationToken);
    Task<ExamDetailDto> PublishAsync(Guid id, CancellationToken cancellationToken);
    Task ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<BulkArchiveResultDto> BulkArchiveAsync(BulkArchiveRequest request, CancellationToken cancellationToken);
    Task<ExamDetailDto> CloneAsync(Guid id, CancellationToken cancellationToken);
    Task<InitFileUploadResponse> InitFileAsync(Guid examId, InitFileUploadRequest request, CancellationToken cancellationToken);
    Task UploadChunkAsync(Guid examId, Guid fileId, int index, Stream content, long contentLength, string? chunkSha256, CancellationToken cancellationToken);
    Task<FileDescriptorDto> FinalizeFileAsync(Guid examId, Guid fileId, FinalizeFileUploadRequest request, CancellationToken cancellationToken);
    Task DeleteFileAsync(Guid examId, Guid fileId, CancellationToken cancellationToken);
    Task<ExamManifestDto> GetManifestAsync(Guid examId, CancellationToken cancellationToken);
    Task<(string Path, string MimeType, string DownloadName)> GetFileContentAsync(Guid examId, Guid fileId, CancellationToken cancellationToken);
}

public interface ISessionService
{
    Task<PagedResult<SessionSummaryDto>> ListAsync(SessionStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<SessionDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<SessionDetailDto> CreateAsync(CreateSessionRequest request, string hostDeviceId, CancellationToken cancellationToken);
    Task<SessionDetailDto> CreateAndOpenAsync(CreateSessionRequest request, string hostDeviceId, CancellationToken cancellationToken);
    Task<CloudProjectionReadiness> GetProjectionReadinessAsync(Guid id, CancellationToken cancellationToken);
    Task<CloudProjectionReadiness> RetryProjectionAsync(Guid id, CancellationToken cancellationToken);
    Task<SessionDetailDto> ChangePublicCloudRoomCodeAsync(Guid id, ChangePublicCloudRoomCodeRequest request, CancellationToken cancellationToken);
    Task<SessionDetailDto> UpdateAsync(Guid id, UpdateSessionRequest request, CancellationToken cancellationToken);
    Task<SessionDetailDto> TransitionAsync(Guid id, SessionStatus target, EndSessionRequest? endRequest, CancellationToken cancellationToken);
    Task<BulkArchiveResultDto> BulkArchiveAsync(BulkArchiveRequest request, CancellationToken cancellationToken);
    Task<JoinSessionResponse> JoinAsync(JoinSessionRequest request, Guid accountUserId, string studentCode, string displayName, string? ipAddress, CancellationToken cancellationToken);
    Task<ParticipantDto> ApproveAsync(Guid sessionId, Guid participantId, Guid mutationRequestId, CancellationToken cancellationToken);
    Task RejectAsync(Guid sessionId, Guid participantId, string? reason, Guid mutationRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ParticipantDto>> BulkApproveAsync(Guid sessionId, BulkApproveRequest request, CancellationToken cancellationToken);
    Task<ParticipantDto> AddExtraTimeAsync(Guid sessionId, Guid participantId, ExtraTimeRequest request, CancellationToken cancellationToken);
    Task<MessageDto> SendMessageAsync(Guid sessionId, SendMessageRequest request, CancellationToken cancellationToken);
    Task<HeartbeatResponse> HeartbeatAsync(Guid sessionId, Guid participantId, string deviceId, HeartbeatRequest request, CancellationToken cancellationToken);
    Task<ParticipantDto> GetParticipantAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken);
}

public interface ISubmissionService
{
    Task<InitSubmissionResponse> InitAsync(InitSubmissionRequest request, CancellationToken cancellationToken);
    Task UploadChunkAsync(Guid submissionId, Guid fileId, int index, Stream content, long contentLength, string? chunkSha256, CancellationToken cancellationToken);
    Task<SubmissionSummaryDto> GetStatusAsync(Guid submissionId, CancellationToken cancellationToken);
    Task<FinalizeSubmissionResponse> FinalizeAsync(Guid submissionId, FinalizeSubmissionRequest request, CancellationToken cancellationToken);
    Task<ReceiptDto> GetReceiptAsync(Guid submissionId, CancellationToken cancellationToken);
    Task<PagedResult<SubmissionSummaryDto>> ListForSessionAsync(Guid sessionId, SubmissionStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<SubmissionSummaryDto> GetAsync(Guid submissionId, CancellationToken cancellationToken);
    Task RejectAsync(Guid submissionId, RejectSubmissionRequest request, CancellationToken cancellationToken);
    Task AllowResubmitAsync(Guid participantId, AllowResubmitRequest request, CancellationToken cancellationToken);
}

public sealed record SubmissionDownloadContent(
    Stream Content,
    string MimeType,
    string DownloadName);

public interface ISubmissionDownloadService
{
    Task<SubmissionDownloadContent> OpenAsync(
        Guid submissionId,
        Guid fileId,
        Guid actorId,
        string? actorOrganizationId,
        string? traceId,
        CancellationToken cancellationToken);
}

public interface IOnlyLanSubmissionDownloadService : ISubmissionDownloadService;

public interface IPublicCloudSubmissionDownloadService : ISubmissionDownloadService;

public interface IGradeService
{
    Task<PagedResult<SubmissionSummaryDto>> GetQueueAsync(GradingStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<GradeDto> GetAsync(Guid submissionId, CancellationToken cancellationToken);
    Task<GradeDto> GetTeacherAsync(Guid submissionId, Guid actorId, string? actorOrganizationId, CancellationToken cancellationToken);
    Task<GradeDto> SaveAsync(Guid submissionId, SaveGradeRequest request, Guid actorId, string? actorOrganizationId, CancellationToken cancellationToken);
    Task<GradeDto> ReturnAsync(Guid submissionId, ReturnGradeRequest request, Guid actorId, string? actorOrganizationId, CancellationToken cancellationToken);
    Task<GradeDto> ReopenAsync(Guid submissionId, ReopenGradeRequest request, Guid actorId, string? actorOrganizationId, CancellationToken cancellationToken);
    Task<FileDescriptorDto> AddAttachmentAsync(Guid submissionId, string fileName, string mimeType, Stream content, long contentLength, Guid actorId, string? actorOrganizationId, CancellationToken cancellationToken);
    Task<byte[]> ExportGradebookCsvAsync(Guid? sessionId, CancellationToken cancellationToken);
}

public interface IStudentResultService
{
    Task<StudentResultPageDto> GetReturnedAsync(
        Guid actorId,
        string? actorOrganizationId,
        int pageSize,
        StudentResultCursorDto? cursor,
        CancellationToken cancellationToken);
}

public interface IQuizGradingService
{
    Task<PagedResult<GradingWorkItemDto>> GetWorkItemsAsync(
        GradingStatus? status,
        int page,
        int pageSize,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken);
    Task<QuizGradeDetailDto> GetAsync(
        Guid attemptId,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken);
    Task<QuizGradeDetailDto> SaveAsync(
        Guid attemptId,
        SaveQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken);
    Task<QuizGradeDetailDto> ReturnAsync(
        Guid attemptId,
        ReturnQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken);
    Task<QuizGradeDetailDto> ReopenAsync(
        Guid attemptId,
        ReopenQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken);
    Task<StudentQuizReviewDto> GetStudentReviewAsync(
        Guid attemptId,
        Guid participantId,
        CancellationToken cancellationToken);
}

public interface ISubmissionPreviewService
{
    Task<SubmissionPreviewManifestDto> GetManifestAsync(
        Guid submissionId,
        Guid fileId,
        string? organizationId,
        CancellationToken cancellationToken);
    Task<SubmissionPreviewDto> GetPreviewAsync(
        Guid submissionId,
        Guid fileId,
        string? entryKey,
        string? organizationId,
        CancellationToken cancellationToken);
}

public interface IControlService
{
    Task<ControlPolicyDto?> GetPolicyAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<ControlPolicyDto> SavePolicyAsync(Guid sessionId, SaveControlPolicyRequest request, CancellationToken cancellationToken);
    Task ApplyPolicyAsync(Guid sessionId, ApplyControlPolicyRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceControlStatusDto>> GetDeviceStatusAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<PagedResult<ViolationDto>> GetViolationsAsync(Guid sessionId, ViolationSeverity? severity, int page, int pageSize, CancellationToken cancellationToken);
    Task<ViolationDto> ReportViolationAsync(Guid sessionId, Guid participantId, ViolationReportRequest request, CancellationToken cancellationToken);
    Task AcknowledgeAsync(Guid violationId, Guid? actorId, CancellationToken cancellationToken);
    Task PolicyAckAsync(Guid sessionId, Guid participantId, PolicyApplyAckRequest request, CancellationToken cancellationToken);
    Task ControlActionAsync(Guid participantId, ControlActionRequest request, CancellationToken cancellationToken);
}

public interface IExportService
{
    Task<ExportJobDto> CreateAsync(CreateExportRequest request, CancellationToken cancellationToken);
    Task<ExportJobDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task CancelAsync(Guid id, CancellationToken cancellationToken);
    Task<(string Path, string FileName)> GetDownloadAsync(Guid id, CancellationToken cancellationToken);
}

public interface IBackupService
{
    Task<BackupDto> CreateAsync(CreateBackupRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupDto>> ListAsync(CancellationToken cancellationToken);
    Task<BackupDto> ValidateAsync(Guid id, CancellationToken cancellationToken);
    Task<RestoreScheduledDto> ScheduleRestoreAsync(Guid id, RestoreBackupRequest request, CancellationToken cancellationToken);
    Task<(string Path, string FileName)> GetDownloadAsync(Guid id, CancellationToken cancellationToken);
}

public interface ISystemService
{
    Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken);
    Task<SystemStatusDto> PreflightAsync(CancellationToken cancellationToken);
    Task<object> GetDiagnosticsAsync(CancellationToken cancellationToken);
    Task<DashboardSummaryDto> GetDashboardAsync(CancellationToken cancellationToken);
    Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken);
    Task<SettingsDto> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken);
    Task<CloudSyncStatusDto> GetCloudStatusAsync(CancellationToken cancellationToken);
    Task TriggerCloudSyncAsync(CancellationToken cancellationToken);
}

public interface IQuizService
{
    Task<QuizImportResultDto> ImportAsync(Guid examId, QuizImportFileRequest request, CancellationToken cancellationToken);
    Task<QuizImportPreviewDto> PreviewImportAsync(Guid examId, Guid teacherId, QuizImportPreviewRequest request, CancellationToken cancellationToken);
    Task<QuizImportResultDto> CommitImportAsync(Guid examId, Guid teacherId, QuizImportCommitRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuizAttemptDto>> ListAttemptsForSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<QuizAttemptDto?> GetAttemptAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken);
    Task<QuizAttemptDto> StartOrGetAttemptAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken);
    Task<SyncQuizAnswersResultDto> SyncAnswersAsync(Guid attemptId, Guid participantId, SyncQuizAnswersRequest request, CancellationToken cancellationToken);
    Task<QuizAttemptDto> FinalizeAsync(Guid attemptId, Guid participantId, FinalizeQuizAttemptRequest request, CancellationToken cancellationToken);
}

public sealed class ApiException(string code, string message, int statusCode = 400, IReadOnlyDictionary<string, string[]>? fieldErrors = null, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; } = fieldErrors;
    public object? Details { get; } = details;
}
