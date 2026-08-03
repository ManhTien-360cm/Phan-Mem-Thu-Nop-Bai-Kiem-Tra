using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public interface IBackendClient
{
    Uri BaseAddress { get; }
    bool HasTrustedAccountToken { get; }
    bool TrySetBaseAddress(string hostOrUrl, int port, out string? error);
    Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default);
    Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default);
    Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default);
    Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default);
    Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default);
    Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default);
    Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default);
    Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default);
    Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default);
    Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default);
    Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default);
    Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default);
    Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default);
    Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default);
    Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default);
    Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default);
    Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default);
    void SetBearerToken(string? token);
    void SetAccountToken(string? token);
    void SetParticipantToken(string? token);
}

public interface ILanDiscoveryService
{
    Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
        TimeSpan timeout,
        string? roomCode = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
        string roomCode,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed record LanDiscoverySnapshot(
    IReadOnlyList<DiscoveryServerDto> Servers,
    IReadOnlyList<OpenSessionDiscoveryDto> Rooms,
    string RequestId,
    int ResponseCount,
    IReadOnlyList<string>? RejectionCodes = null);

public sealed class LanDiscoveryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum StudentConnectionState { Stopped, Connecting, Online, Reconnecting, Offline, AuthenticationExpired }

public interface IStudentHeartbeatService : IDisposable
{
    StudentConnectionState State { get; }
    event EventHandler<StudentConnectionState>? StateChanged;
    void Start();
    void Stop();
    Task<bool> ProbeNowAsync(CancellationToken ct = default);
}

public interface IStudentRealtimeService : IDisposable
{
    bool IsConnected { get; }
    bool IsRunning => IsConnected;
    Guid? ActiveSessionId => null;
    event EventHandler<string>? EventReceived;
    event EventHandler<StudentRealtimeNotification>? NotificationReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed record StudentRealtimeNotification(
    Guid SessionId,
    string EventName,
    long Revision,
    TimeExtendedEvent? TimeExtended,
    Guid? ParticipantId = null,
    PublicCloudProjectionUpdatedEvent? ProjectionUpdated = null,
    StudentNotificationEventDto? StudentNotification = null);

public interface ISubmissionRecoveryService : IDisposable
{
    int PendingCount { get; }
    event EventHandler<int>? PendingCountChanged;
    event EventHandler<ExamTransfer.Desktop.Infrastructure.SubmissionProgressSnapshot>? ProgressChanged;
    void Start();
    void Trigger();
}

public interface IFileDialogService { string? PickFile(string filter); }
public interface IFolderDialogService { string? PickFolder(); }
public interface IDialogService { bool Confirm(string title, string message); }
public interface ILocalFileLauncher
{
    bool Exists(string path);
    void Open(string path);
}
public interface IToastService { void Show(string message, string tone = "info"); }
public interface IClipboardService { void SetText(string text); }
public interface ILocalPreferenceService { string? Get(string key); void Set(string key, string value); }

public interface IRealtimeService
{
    bool IsConnected { get; }
    event EventHandler<string>? EventReceived;
    event EventHandler<StudentRealtimeNotification>? NotificationReceived;
    Task ConnectAsync(string? token = null, CancellationToken ct = default);
    Task SubscribeSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task UnsubscribeSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}
