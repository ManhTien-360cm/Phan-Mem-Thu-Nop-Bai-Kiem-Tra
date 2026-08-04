using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SubmissionSelectionTests
{
    [Fact]
    public void SelectionRow_ProjectsSubmissionAndRaisesForIsSelectedOnlyWhenChanged()
    {
        var submittedAt = new DateTimeOffset(2026, 8, 1, 8, 30, 0, TimeSpan.Zero);
        var submission = MakeSubmission(
            "HS001",
            isLate: true,
            submittedAt,
            TransferStatus.Completed,
            TransferStatus.Running);
        var row = new SubmissionSelectionRow(submission);
        var changes = new List<string?>();
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        row.IsSelected = true;
        row.IsSelected = true;

        Assert.Same(submission, row.Submission);
        Assert.Equal(submission.Id, row.SubmissionId);
        Assert.Equal(submission.StudentCode, row.StudentCode);
        Assert.Equal(submission.DisplayName, row.StudentName);
        Assert.Equal(submission.AttemptNumber, row.AttemptNumber);
        Assert.Equal(submittedAt, row.SubmittedAt);
        Assert.True(row.IsLate);
        Assert.Equal(submission.Status, row.Status);
        Assert.Equal(1, row.CompletedFileCount);
        Assert.True(row.CanDownload);
        Assert.Equal([nameof(SubmissionSelectionRow.IsSelected)], changes);
    }

    [Fact]
    public async Task SelectionCommands_TrackSingleMultipleAllAndClearWithDownloadEligibility()
    {
        var session = MakeSession();
        var downloadable = MakeSubmission(
            "HS001",
            isLate: false,
            DateTimeOffset.UtcNow,
            TransferStatus.Completed);
        var unavailable = MakeSubmission(
            "HS002",
            isLate: true,
            DateTimeOffset.UtcNow,
            TransferStatus.Running);
        var secondDownloadable = MakeSubmission(
            "HS003",
            isLate: false,
            DateTimeOffset.UtcNow,
            TransferStatus.Completed);
        var api = new SubmissionBackendClient(session)
        {
            Submissions = [downloadable, unavailable, secondDownloadable]
        };
        using var viewModel = new SubmissionCenterViewModel(api, new SilentRealtimeService());
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.Equal(0, viewModel.DownloadableSelectedCount);
        Assert.False(viewModel.HasSelection);
        Assert.False(viewModel.HasDownloadableSelection);
        Assert.False(viewModel.AllVisibleSelected);
        Assert.True(viewModel.SelectAllCommand.CanExecute(null));
        Assert.False(viewModel.ClearSelectionCommand.CanExecute(null));
        Assert.False(viewModel.DownloadSelectedCommand.CanExecute(null));

        viewModel.Submissions[1].IsSelected = true;
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Equal(0, viewModel.DownloadableSelectedCount);
        Assert.True(viewModel.HasSelection);
        Assert.False(viewModel.HasDownloadableSelection);
        Assert.True(viewModel.ClearSelectionCommand.CanExecute(null));
        Assert.True(viewModel.DownloadSelectedCommand.CanExecute(null));
        Assert.True(viewModel.Submissions[1].IsLate);

        viewModel.Submissions[0].IsSelected = true;
        Assert.Equal(2, viewModel.SelectedCount);
        Assert.Equal(1, viewModel.DownloadableSelectedCount);
        Assert.True(viewModel.DownloadSelectedCommand.CanExecute(null));

        viewModel.SelectAllCommand.Execute(null);
        Assert.Equal(3, viewModel.SelectedCount);
        Assert.Equal(2, viewModel.DownloadableSelectedCount);
        Assert.True(viewModel.AllVisibleSelected);
        Assert.False(viewModel.SelectAllCommand.CanExecute(null));

        viewModel.ClearSelectionCommand.Execute(null);
        Assert.All(viewModel.Submissions, row => Assert.False(row.IsSelected));
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.Equal(0, viewModel.DownloadableSelectedCount);
        Assert.False(viewModel.HasSelection);
        Assert.False(viewModel.ClearSelectionCommand.CanExecute(null));
        Assert.False(viewModel.DownloadSelectedCommand.CanExecute(null));
        Assert.True(viewModel.Submissions[1].IsLate);
    }

    [Fact]
    public async Task RefreshRestoresSelectionBySubmissionIdAndDropsRemovedRows()
    {
        var session = MakeSession();
        var first = MakeSubmission(
            "HS001",
            isLate: false,
            DateTimeOffset.UtcNow,
            TransferStatus.Completed);
        var second = MakeSubmission(
            "HS002",
            isLate: false,
            DateTimeOffset.UtcNow,
            TransferStatus.Completed);
        var third = MakeSubmission(
            "HS003",
            isLate: true,
            DateTimeOffset.UtcNow,
            TransferStatus.Completed);
        var replacement = MakeSubmission(
            "HS004",
            isLate: false,
            DateTimeOffset.UtcNow,
            TransferStatus.Completed);
        var api = new SubmissionBackendClient(session)
        {
            Submissions = [first, second, third]
        };
        using var viewModel = new SubmissionCenterViewModel(api, new SilentRealtimeService());
        await viewModel.InitializeAsync(CancellationToken.None);
        var removedRow = viewModel.Submissions[0];
        removedRow.IsSelected = true;
        viewModel.Submissions[2].IsSelected = true;
        Assert.Equal(2, viewModel.SelectedCount);

        api.Submissions = [replacement, third, second];
        viewModel.LoadCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.SubmissionRequests == 2 && !viewModel.IsBusy,
            TimeSpan.FromSeconds(3)));

        Assert.Equal(
            [third.Id],
            viewModel.Submissions
                .Where(row => row.IsSelected)
                .Select(row => row.SubmissionId)
                .ToArray());
        Assert.False(viewModel.Submissions[0].IsSelected);
        Assert.Equal(replacement.Id, viewModel.Submissions[0].SubmissionId);
        Assert.True(viewModel.Submissions[1].IsLate);
        Assert.Equal(1, viewModel.SelectedCount);

        removedRow.IsSelected = false;
        Assert.Equal(1, viewModel.SelectedCount);
    }

    [Fact]
    public async Task ResubmitIsDisabledUntilLatestAttemptIsRejected()
    {
        var session = MakeSession();
        var submitted = MakeSubmission(
            "HS001", false, DateTimeOffset.UtcNow, TransferStatus.Completed);
        var rejected = submitted with { Status = SubmissionStatus.Rejected };
        var api = new SubmissionBackendClient(session) { Submissions = [submitted] };
        using var viewModel = new SubmissionCenterViewModel(api, new SilentRealtimeService());
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.ResubmitCommand.CanExecute(null));
        Assert.True(viewModel.RejectCommand.CanExecute(null));
        Assert.Contains("từ chối attempt", viewModel.ResubmitGuidance, StringComparison.OrdinalIgnoreCase);

        api.Submissions = [rejected];
        viewModel.LoadCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.SubmissionRequests == 2 && !viewModel.IsBusy,
            TimeSpan.FromSeconds(3)));

        Assert.True(viewModel.ResubmitCommand.CanExecute(null));
        Assert.False(viewModel.RejectCommand.CanExecute(null));
    }

    [Fact]
    public void ProductionXaml_SeparatesInteractiveSelectionFromReadOnlyLateFlag()
    {
        var xaml = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "SubmissionCenterView.xaml"));

        Assert.Contains("Header=\"CHỌN\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsSelected, Mode=TwoWay", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"THỜI HẠN\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding LateDisplay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"NGUỒN\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UseComputedLateCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkLateCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkOnTimeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectAllCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ClearSelectionCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Đã chọn", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedCount", xaml, StringComparison.Ordinal);
    }

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private static SessionSummaryDto MakeSession() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Kỳ thi",
        "ROOM42",
        SessionStatus.Collecting,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        new SessionCountsDto(4, 0, 4, 4, 3, 0, 0),
        1,
        "rv-session");

    private static SubmissionSummaryDto MakeSubmission(
        string studentCode,
        bool isLate,
        DateTimeOffset submittedAt,
        params TransferStatus[] fileStatuses) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            studentCode,
            "Học sinh " + studentCode,
            1,
            isLate ? SubmissionStatus.LateSubmitted : SubmissionStatus.Submitted,
            submittedAt.AddSeconds(-1),
            submittedAt,
            submittedAt.AddMinutes(-1),
            isLate,
            "RC-" + studentCode,
            true,
            fileStatuses.Select((status, index) => new SubmissionFileDto(
                Guid.NewGuid(),
                $"file-{index + 1}.zip",
                100,
                $"sha-{index + 1}",
                "application/zip",
                1,
                status == TransferStatus.Completed ? [0] : [],
                status,
                null)).ToList());

    private sealed class SilentRealtimeService : IRealtimeService
    {
        public bool IsConnected { get; private set; }
        public event EventHandler<string>? EventReceived;
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(string? token = null, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SubscribeSessionAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnsubscribeSessionAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            EventReceived?.Invoke(this, "Disconnected");
            return Task.CompletedTask;
        }
    }

    private sealed class SubmissionBackendClient(SessionSummaryDto session) : IBackendClient
    {
        public IReadOnlyList<SubmissionSummaryDto> Submissions { get; set; } = [];
        public int SubmissionRequests { get; private set; }
        public Uri BaseAddress { get; } = new("http://localhost:5048/");
        public bool HasTrustedAccountToken => true;

        public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(
                ApiResponse<PagedResult<SessionSummaryDto>>.Ok(
                    new([session], 1, 50, 1),
                    "test"));

        public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(
            Guid sessionId,
            CancellationToken ct = default)
        {
            SubmissionRequests++;
            return Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(
                ApiResponse<PagedResult<SubmissionSummaryDto>>.Ok(
                    new(Submissions, 1, 50, Submissions.Count),
                    "test"));
        }

        public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error)
        {
            error = null;
            return true;
        }

        public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SystemStatusDto>?>(null);
        public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<DashboardSummaryDto>?>(null);
        public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<ClassSummaryDto>>?>(null);
        public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<ExamSummaryDto>>?>(null);
        public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ApiResponse<SessionDetailDto>?>(null);
        public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<CloudSyncStatusDto>?>(null);
        public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SettingsDto>?>(null);
        public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default) => Task.FromResult<ApiResponse<T>?>(null);
        public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default) => Task.FromResult<ApiResponse<object>?>(null);
        public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public void SetBearerToken(string? token) { }
        public void SetAccountToken(string? token) { }
        public void SetParticipantToken(string? token) { }
    }
}
