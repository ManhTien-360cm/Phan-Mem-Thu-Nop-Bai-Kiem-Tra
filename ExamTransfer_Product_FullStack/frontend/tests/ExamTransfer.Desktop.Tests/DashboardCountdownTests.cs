using System.IO;
using System.Text.Json;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class DashboardCountdownTests
{
    [Theory]
    [InlineData(SessionStatus.Finished, "Đã kết thúc")]
    [InlineData(SessionStatus.Cancelled, "Đã hủy")]
    [InlineData(SessionStatus.Archived, "Đã lưu trữ")]
    public async Task TerminalSession_ShowsLocalizedStateAndStopsCountdown(
        SessionStatus status,
        string expectedText)
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(serverUtc, status);
        using var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);

        var card = Assert.IsType<ActiveSessionCard>(viewModel.ActiveSession);
        Assert.Equal(status, card.Status);
        Assert.Equal(expectedText, card.StatusDisplayText);
        Assert.True(card.IsTerminal);
        Assert.False(card.IsCountdownVisible);
        Assert.Equal(expectedText, card.TimeLeftLabel);
        Assert.Equal(expectedText, card.TimeLeft);
        Assert.False(ticker.IsRunning);

        var terminalTime = card.TimeLeft;
        source.Advance(TimeSpan.FromMinutes(5));
        ticker.Fire();

        Assert.Equal(terminalTime, card.TimeLeft);
    }

    [Theory]
    [InlineData(SessionStatus.Waiting, "Đang chờ")]
    [InlineData(SessionStatus.InProgress, "Đang diễn ra")]
    [InlineData(SessionStatus.Paused, "Tạm dừng")]
    [InlineData(SessionStatus.Collecting, "Đang thu bài")]
    public async Task CountdownState_UsesDeadlineAndTicker(
        SessionStatus status,
        string expectedText)
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(
            serverUtc,
            status,
            serverUtc.AddSeconds(100));
        using var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);

        var card = Assert.IsType<ActiveSessionCard>(viewModel.ActiveSession);
        Assert.Equal(status, card.Status);
        Assert.Equal(expectedText, card.StatusDisplayText);
        Assert.False(card.IsTerminal);
        Assert.True(card.IsCountdownVisible);
        Assert.Equal("Thời gian còn lại", card.TimeLeftLabel);
        Assert.Equal("00:01:40", card.TimeLeft);
        Assert.True(ticker.IsRunning);

        source.Advance(TimeSpan.FromSeconds(10));
        ticker.Fire();

        Assert.Equal("00:01:30", card.TimeLeft);
    }

    [Fact]
    public async Task ActiveProjection_WinsWhenFirstRecentSessionIsFinished()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(
            serverUtc,
            SessionStatus.InProgress,
            serverUtc.AddSeconds(100),
            SessionStatus.Finished);
        using var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);

        var active = Assert.IsType<ActiveSessionCard>(viewModel.ActiveSession);
        Assert.Equal(SessionStatus.InProgress, active.Status);
        Assert.True(ticker.IsRunning);
        var history = Assert.Single(viewModel.Activities);
        Assert.Equal("History session", history.Title);
        Assert.Contains("HISTORY", history.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullActiveProjection_DoesNotFallbackToFinishedHistoryAndStopsTicker()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(
            serverUtc,
            sessionStatus: null,
            recentSessionStatus: SessionStatus.Finished);
        ticker.Start();
        using var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.ActiveSession);
        Assert.False(viewModel.HasActiveSession);
        Assert.False(ticker.IsRunning);
        Assert.Equal("History session", Assert.Single(viewModel.Activities).Title);
    }

    [Fact]
    public async Task LegacyDashboardPayloadWithoutActiveSession_DeserializesAsNullWithoutFallback()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyJson = JsonSerializer.Serialize(
            new
            {
                classCount = 1,
                examCount = 1,
                activeSessionCount = 0,
                pendingGradingCount = 0,
                storageBytes = 0,
                recentSessions = new[]
                {
                    DashboardSession(
                        serverUtc,
                        SessionStatus.Finished,
                        "History session",
                        "HISTORY")
                },
                warnings = Array.Empty<string>()
            },
            options);
        var dashboard = JsonSerializer.Deserialize<DashboardSummaryDto>(legacyJson, options);
        var api = new RecordingBackendClient(serverUtc, sessionStatus: null)
        {
            DashboardResponse = dashboard
        };
        using var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.NotNull(dashboard);
        Assert.Null(dashboard.ActiveSession);
        Assert.Null(viewModel.ActiveSession);
        Assert.False(ticker.IsRunning);
        Assert.Equal("History session", Assert.Single(viewModel.Activities).Title);
    }

    [Fact]
    public async Task PastDeadline_ClampsAtZeroAndNeverDisplaysNegativeTime()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(
            serverUtc,
            SessionStatus.InProgress,
            serverUtc.AddSeconds(-5));
        using var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);

        var card = Assert.IsType<ActiveSessionCard>(viewModel.ActiveSession);
        Assert.Equal("00:00:00", card.TimeLeft);
        Assert.DoesNotContain('-', card.TimeLeft);

        source.Advance(TimeSpan.FromMinutes(1));
        ticker.Fire();

        Assert.Equal("00:00:00", card.TimeLeft);
        Assert.DoesNotContain('-', card.TimeLeft);
    }

    [Fact]
    public void ProductionXaml_HidesEntireCountdownForNonCountdownState()
    {
        var xaml = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "DashboardView.xaml"));

        Assert.Contains("ActiveSession.StatusDisplayText", xaml, StringComparison.Ordinal);
        Assert.Contains("ActiveSession.IsCountdownVisible", xaml, StringComparison.Ordinal);
        Assert.Contains("ActiveSession.TimeLeftLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding ActiveSession.Status}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TickUpdatesObservableTimeWithoutNetworkAndDisposeStopsCallbacks()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(serverUtc);
        var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Equal("00:01:40", viewModel.ActiveSession?.TimeLeft);
        Assert.Equal(1, api.DashboardRequests);
        Assert.True(ticker.IsRunning);

        source.Advance(TimeSpan.FromSeconds(10));
        ticker.Fire();

        Assert.Equal("00:01:30", viewModel.ActiveSession?.TimeLeft);
        Assert.Equal(1, api.DashboardRequests);

        viewModel.Dispose();
        var afterDispose = viewModel.ActiveSession?.TimeLeft;
        source.Advance(TimeSpan.FromSeconds(10));
        ticker.Fire();

        Assert.True(ticker.Disposed);
        Assert.Equal(afterDispose, viewModel.ActiveSession?.TimeLeft);
        Assert.Equal(1, api.DashboardRequests);
    }

    private static SessionSummaryDto DashboardSession(
        DateTimeOffset serverUtc,
        SessionStatus status,
        string title,
        string roomCode,
        DateTimeOffset? effectiveDeadlineUtc = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            title,
            roomCode,
            status,
            serverUtc,
            serverUtc.AddMinutes(-1),
            status is SessionStatus.Finished or SessionStatus.Cancelled
                ? serverUtc
                : null,
            effectiveDeadlineUtc,
            new SessionCountsDto(10, 0, 10, 8, 2, 0, 0),
            1,
            "v1");

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}

internal sealed class FakeCountdownTicker : ICountdownTicker
{
    private EventHandler? tick;

    public event EventHandler? Tick
    {
        add => tick += value;
        remove => tick -= value;
    }

    public bool IsRunning { get; private set; }
    public bool Disposed { get; private set; }

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void Fire() => tick?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        Disposed = true;
        IsRunning = false;
        tick = null;
    }
}

internal sealed class RecordingBackendClient(
    DateTimeOffset serverUtc,
    SessionStatus? sessionStatus = SessionStatus.InProgress,
    DateTimeOffset? effectiveDeadlineUtc = null,
    SessionStatus? recentSessionStatus = null) : IBackendClient
{
    public int DashboardRequests { get; private set; }
    public DashboardSummaryDto? DashboardResponse { get; init; }
    public QuizAttemptDto? QuizAttemptResponse { get; init; }
    public ClassDetailDto? ClassDetailResponse { get; init; }
    public ExamSummaryDto? ExamSummaryResponse { get; init; }
    public ExamDetailDto? ExamDetailResponse { get; init; }
    public SessionDetailDto? SessionDetailResponse { get; set; }
    public ParticipantDto? ParticipantResponse { get; set; }
    public IReadOnlyList<ClassSummaryDto>? ClassResponses { get; init; }
    public IReadOnlyList<ExamSummaryDto>? ExamResponses { get; init; }
    public IReadOnlyList<SessionSummaryDto>? SessionResponses { get; init; }
    public Queue<CloudProjectionReadinessView> ProjectionResponses { get; } = [];
    public List<string> PostPaths { get; } = [];
    public List<object?> PostRequests { get; } = [];
    public List<string> PutPaths { get; } = [];
    public List<object?> PutRequests { get; } = [];
    public Uri BaseAddress { get; } = new("https://localhost/");
    public bool HasTrustedAccountToken => false;

    public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default)
    {
        DashboardRequests++;
        if (DashboardResponse is not null)
        {
            return Task.FromResult<ApiResponse<DashboardSummaryDto>?>(
                ApiResponse<DashboardSummaryDto>.Ok(DashboardResponse, "test"));
        }

        IReadOnlyList<SessionSummaryDto> sessions = sessionStatus is { } status
            ?
            [
                new SessionSummaryDto(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Kỳ thi",
                    "ROOM",
                    status,
                    serverUtc,
                    serverUtc.AddMinutes(-1),
                    null,
                    effectiveDeadlineUtc ?? serverUtc.AddSeconds(100),
                    new SessionCountsDto(10, 0, 10, 8, 2, 0, 0),
                    1,
                    "v1")
            ]
            : [];
        var activeSession = sessions.FirstOrDefault();
        IReadOnlyList<SessionSummaryDto> recentSessions = recentSessionStatus is { } recentStatus
            ?
            [
                CreateDashboardSession(
                    recentStatus,
                    "History session",
                    "HISTORY",
                    effectiveDeadlineUtc)
            ]
            : [];
        return Task.FromResult<ApiResponse<DashboardSummaryDto>?>(ApiResponse<DashboardSummaryDto>.Ok(
            new DashboardSummaryDto(
                1,
                1,
                activeSession is null ? 0 : 1,
                0,
                0,
                recentSessions,
                [],
                activeSession),
            "test"));
    }

    private SessionSummaryDto CreateDashboardSession(
        SessionStatus status,
        string title,
        string roomCode,
        DateTimeOffset? deadlineUtc) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            title,
            roomCode,
            status,
            serverUtc,
            serverUtc.AddMinutes(-1),
            status is SessionStatus.Finished or SessionStatus.Cancelled
                ? serverUtc
                : null,
            deadlineUtc,
            new SessionCountsDto(10, 0, 10, 8, 2, 0, 0),
            1,
            "v1");

    public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error) { error = null; return true; }
    public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SystemStatusDto>?>(null);
    public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) =>
        Task.FromResult<ApiResponse<PagedResult<ClassSummaryDto>>?>(
            ClassResponses is not null
                ? ApiResponse<PagedResult<ClassSummaryDto>>.Ok(
                    new(ClassResponses, 1, 50, ClassResponses.Count),
                    "test")
                : ExamResponses is not null || ExamSummaryResponse is not null
                ? ApiResponse<PagedResult<ClassSummaryDto>>.Ok(new([], 1, 50, 0), "test")
                : null);
    public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) =>
        Task.FromResult<ApiResponse<PagedResult<ExamSummaryDto>>?>(
            ExamResponses is not null
                ? ApiResponse<PagedResult<ExamSummaryDto>>.Ok(new(ExamResponses, 1, 50, ExamResponses.Count), "test")
                : ExamSummaryResponse is null
                    ? null
                    : ApiResponse<PagedResult<ExamSummaryDto>>.Ok(new([ExamSummaryResponse], 1, 50, 1), "test"));
    public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) =>
        Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(
            SessionResponses is null
                ? null
                : ApiResponse<PagedResult<SessionSummaryDto>>.Ok(new(SessionResponses, 1, 50, SessionResponses.Count), "test"));
    public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<ApiResponse<SessionDetailDto>?>(
            SessionDetailResponse is null
                ? null
                : ApiResponse<SessionDetailDto>.Ok(SessionDetailResponse, "test"));
    public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(null);
    public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<CloudSyncStatusDto>?>(null);
    public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SettingsDto>?>(null);
    public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        if (ClassDetailResponse is not null && typeof(T) == typeof(ClassDetailDto))
            return Task.FromResult<ApiResponse<T>?>(
                ApiResponse<T>.Ok((T)(object)ClassDetailResponse, "test"));
        if (ExamDetailResponse is not null && typeof(T) == typeof(ExamDetailDto))
            return Task.FromResult<ApiResponse<T>?>(
                ApiResponse<T>.Ok((T)(object)ExamDetailResponse, "test"));
        if (ParticipantResponse is not null && typeof(T) == typeof(ParticipantDto))
            return Task.FromResult<ApiResponse<T>?>(
                ApiResponse<T>.Ok((T)(object)ParticipantResponse, "test"));
        if (typeof(T) == typeof(CloudProjectionReadinessView) && ProjectionResponses.Count > 0)
            return Task.FromResult<ApiResponse<T>?>(
                ApiResponse<T>.Ok((T)(object)ProjectionResponses.Dequeue(), "test"));
        return Task.FromResult<ApiResponse<T>?>(null);
    }
    public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        PostPaths.Add(path);
        PostRequests.Add(request);
        if (SessionDetailResponse is not null && typeof(TResponse) == typeof(SessionDetailDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)SessionDetailResponse, "test"));
        if (ExamDetailResponse is not null && typeof(TResponse) == typeof(ExamDetailDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)ExamDetailResponse, "test"));
        if (QuizAttemptResponse is not null && typeof(TResponse) == typeof(QuizAttemptDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)QuizAttemptResponse, "test"));
        if (request is BulkArchiveRequest bulk && typeof(TResponse) == typeof(BulkArchiveResultDto))
        {
            var result = new BulkArchiveResultDto(
                bulk.Ids.Count,
                bulk.Ids.Count,
                [],
                []);
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)result, "test"));
        }
        return Task.FromResult<ApiResponse<TResponse>?>(null);
    }
    public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        PutPaths.Add(path);
        PutRequests.Add(request);
        if (ExamDetailResponse is not null && typeof(TResponse) == typeof(ExamDetailDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)ExamDetailResponse, "test"));
        if (SessionDetailResponse is not null && typeof(TResponse) == typeof(SessionDetailDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)SessionDetailResponse, "test"));
        return Task.FromResult<ApiResponse<TResponse>?>(null);
    }
    public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
    public Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default) => Task.FromResult<ApiResponse<object>?>(null);
    public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public void SetBearerToken(string? token) { }
    public void SetAccountToken(string? token) { }
    public void SetParticipantToken(string? token) { }
}
