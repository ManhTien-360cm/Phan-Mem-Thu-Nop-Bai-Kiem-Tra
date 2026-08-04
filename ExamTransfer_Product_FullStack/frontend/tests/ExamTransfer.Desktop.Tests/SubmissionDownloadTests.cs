using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SubmissionDownloadTests
{
    [Fact]
    public async Task BatchDownload_DownloadsAllCompletedFilesAndContinuesAfterAFileFailure()
    {
        using var destination = new TemporaryDirectory();
        var first = MakeSubmission("HS001", "Nguyen An", 1,
            ("bai.docx", TransferStatus.Completed),
            ("anh.png", TransferStatus.Completed));
        var second = MakeSubmission("HS002", "Tran Binh", 2,
            ("bai.pdf", TransferStatus.Completed),
            ("dang-tai.tmp", TransferStatus.Running));
        var third = MakeSubmission("HS003", "Le Chi", 1,
            ("loi.zip", TransferStatus.Completed),
            ("ket-qua.txt", TransferStatus.Completed));
        var failedFile = third.Files[0];
        var api = new RecordingBackendClient
        {
            FailedFileIds = [failedFile.Id],
            OnDownload = (_, path, _) =>
            {
                File.WriteAllText(path, "downloaded");
                return Task.CompletedTask;
            }
        };
        var downloader = new SubmissionBatchDownloader(api);

        var result = await downloader.DownloadAsync(
            [first, second, third], destination.Path, CancellationToken.None);

        Assert.Equal(5, api.Downloads.Count);
        Assert.All(api.Downloads, call => Assert.Equal("sha", call.ExpectedSha256));
        Assert.All(api.Downloads, call => Assert.DoesNotContain("dang-tai.tmp", call.Destination));
        Assert.Equal(4, result.SuccessfulFileCount);
        Assert.Equal(1, result.FailedFileCount);
        Assert.Equal(2, result.FullySuccessfulSubmissionCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(third.Id, failure.SubmissionId);
        Assert.Equal(failedFile.Id, failure.FileId);
        Assert.Contains("loi.zip", failure.DisplayName, StringComparison.Ordinal);
        Assert.Contains("simulated", failure.Error, StringComparison.Ordinal);
        Assert.Contains(api.Downloads[^1].FileId, new[] { third.Files[1].Id });
        Assert.Equal(4, api.Downloads.Count(call => File.Exists(call.Destination)));
    }

    [Fact]
    public async Task BatchDownload_ReportsMultipleFailuresAndStillAttemptsRemainingFiles()
    {
        using var destination = new TemporaryDirectory();
        var submission = MakeSubmission("HS001", "Nguyen An", 1,
            ("one.zip", TransferStatus.Completed),
            ("two.zip", TransferStatus.Completed),
            ("three.zip", TransferStatus.Completed),
            ("four.zip", TransferStatus.Completed));
        var api = new RecordingBackendClient
        {
            FailedFileIds = [submission.Files[0].Id, submission.Files[2].Id]
        };

        var result = await new SubmissionBatchDownloader(api).DownloadAsync(
            [submission], destination.Path, CancellationToken.None);

        Assert.Equal(4, api.Downloads.Count);
        Assert.Equal(2, result.SuccessfulFileCount);
        Assert.Equal(2, result.FailedFileCount);
        Assert.Equal(2, result.Failures.Count);
        Assert.Equal(0, result.FullySuccessfulSubmissionCount);
    }

    [Fact]
    public async Task BatchDownload_CleansFallbackReservedLongAndDuplicateNames()
    {
        using var destination = new TemporaryDirectory();
        var longName = new string('x', 180) + ".submission";
        var submission = MakeSubmission("", "CON", 7,
            ("report?.txt", TransferStatus.Completed),
            ("report?.txt", TransferStatus.Completed),
            ("", TransferStatus.Completed),
            ("NUL", TransferStatus.Completed),
            ("README", TransferStatus.Completed),
            (longName, TransferStatus.Completed));
        var api = new RecordingBackendClient();

        var result = await new SubmissionBatchDownloader(api).DownloadAsync(
            [submission], destination.Path, CancellationToken.None);

        Assert.Equal(6, result.SuccessfulFileCount);
        Assert.Equal(0, result.FailedFileCount);
        Assert.Equal(1, result.FullySuccessfulSubmissionCount);
        Assert.Single(api.Downloads.Select(call => Directory.GetParent(call.Destination)!.FullName).Distinct());
        Assert.All(api.Downloads, call =>
        {
            var attemptFolder = Directory.GetParent(call.Destination)!;
            Assert.Equal("Lan_7", attemptFolder.Name);
            Assert.StartsWith("Khong_ma_", attemptFolder.Parent!.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetInvalidFileNameChars(), character =>
                attemptFolder.Parent.Name.Contains(character)
                || System.IO.Path.GetFileName(call.Destination).Contains(character));
            Assert.InRange(System.IO.Path.GetFileName(call.Destination).Length, 1, 120);
        });
        var fileNames = api.Downloads.Select(call => System.IO.Path.GetFileName(call.Destination)).ToArray();
        Assert.Equal(fileNames.Length, fileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("README", fileNames);
        Assert.DoesNotContain("NUL", fileNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchDownload_NoCompletedFilesReturnsEmptySummaryWithoutCallingApi()
    {
        using var destination = new TemporaryDirectory();
        var api = new RecordingBackendClient();
        var submission = MakeSubmission("HS001", "Nguyen An", 1,
            ("pending.zip", TransferStatus.Running),
            ("failed.zip", TransferStatus.Failed));

        var result = await new SubmissionBatchDownloader(api).DownloadAsync(
            [submission], destination.Path, CancellationToken.None);

        Assert.True(result.HasNoCompletedFiles);
        Assert.Empty(api.Downloads);
        Assert.Equal(0, result.FullySuccessfulSubmissionCount);
    }

    [Fact]
    public async Task BatchDownload_CancellationStopsTheBatchAndIsNotRecordedAsAFileFailure()
    {
        using var destination = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var submission = MakeSubmission("HS001", "Nguyen An", 1,
            ("one.zip", TransferStatus.Completed),
            ("two.zip", TransferStatus.Completed));
        var api = new RecordingBackendClient
        {
            OnDownload = (_, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SubmissionBatchDownloader(api).DownloadAsync(
                [submission], destination.Path, cancellation.Token));

        Assert.Single(api.Downloads);
    }

    [Fact]
    public async Task ViewModel_CancelFolderLeavesSelectionAndStatusUntouched()
    {
        var submission = MakeSubmission("HS001", "Nguyen An", 1,
            ("bai.zip", TransferStatus.Completed));
        var api = new RecordingBackendClient { Submissions = [submission] };
        var folders = new RecordingFolderDialog(null);
        using var viewModel = new SubmissionCenterViewModel(api, new SilentRealtimeService(), folders);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.Submissions[0].IsSelected = true;
        var statusBefore = viewModel.Status;

        viewModel.DownloadSelectedCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => folders.PickCount == 1, TimeSpan.FromSeconds(2)));

        Assert.True(viewModel.Submissions[0].IsSelected);
        Assert.Equal(statusBefore, viewModel.Status);
        Assert.Empty(api.Downloads);
        Assert.Equal(1, folders.PickCount);
    }

    [Fact]
    public async Task ViewModel_UsesOneFolderAndSelectionSnapshotForAsyncBatch()
    {
        using var destination = new TemporaryDirectory();
        var first = MakeSubmission("HS001", "Nguyen An", 1,
            ("one.zip", TransferStatus.Completed),
            ("two.zip", TransferStatus.Completed));
        var second = MakeSubmission("HS002", "Tran Binh", 1,
            ("other.zip", TransferStatus.Completed));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new RecordingBackendClient
        {
            Submissions = [first, second],
            OnDownload = async (_, _, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var folders = new RecordingFolderDialog(destination.Path);
        using var viewModel = new SubmissionCenterViewModel(api, new SilentRealtimeService(), folders);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.Submissions[0].IsSelected = true;

        viewModel.DownloadSelectedCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Submissions[0].IsSelected = false;
        viewModel.Submissions[1].IsSelected = true;
        release.TrySetResult();
        Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(3)));

        Assert.Equal(1, folders.PickCount);
        Assert.Equal([first.Files[0].Id, first.Files[1].Id], api.Downloads.Select(call => call.FileId));
        Assert.DoesNotContain(api.Downloads, call => call.SubmissionId == second.Id);
        Assert.Contains("2 file", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_NoSelectionDoesNotOpenFolderAndNoCompletedSelectionIsReported()
    {
        using var destination = new TemporaryDirectory();
        var submission = MakeSubmission("HS001", "Nguyen An", 1,
            ("pending.zip", TransferStatus.Running));
        var api = new RecordingBackendClient { Submissions = [submission] };
        var folders = new RecordingFolderDialog(destination.Path);
        using var viewModel = new SubmissionCenterViewModel(api, new SilentRealtimeService(), folders);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.DownloadSelectedCommand.CanExecute(null));
        viewModel.DownloadSelectedCommand.Execute(null);
        Assert.Equal(0, folders.PickCount);

        viewModel.Submissions[0].IsSelected = true;
        Assert.True(viewModel.DownloadSelectedCommand.CanExecute(null));
        viewModel.DownloadSelectedCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(2)));

        Assert.Equal(1, folders.PickCount);
        Assert.Empty(api.Downloads);
        Assert.Contains("khong co file Completed", RemoveDiacritics(viewModel.Status), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionXaml_UsesDownloadSelectedCommandAndLabel()
    {
        var xaml = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "SubmissionCenterView.xaml"));

        Assert.Contains("Content=\"Tải bài đã chọn\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DownloadSelectedCommand}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tải file đầu tiên", xaml, StringComparison.Ordinal);
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        return new string(normalized.Where(character =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
            != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd')
            .Replace('Đ', 'D');
    }

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = System.IO.Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(System.IO.Path.DirectorySeparatorChar, segments));
    }

    private static SessionSummaryDto MakeSession() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Kỳ thi", "ROOM42", SessionStatus.Collecting,
        DateTimeOffset.UtcNow, null, null, null,
        new SessionCountsDto(3, 0, 3, 3, 3, 0, 0), 1, "rv-session");

    private static SubmissionSummaryDto MakeSubmission(
        string studentCode,
        string studentName,
        int attemptNumber,
        params (string Name, TransferStatus Status)[] files) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), studentCode, studentName,
            attemptNumber, SubmissionStatus.Submitted,
            DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(-1), false, "RC-TEST", true,
            files.Select(file => new SubmissionFileDto(
                Guid.NewGuid(), file.Name, 100, "sha", "application/octet-stream", 1,
                file.Status == TransferStatus.Completed ? [0] : [], file.Status, null)).ToArray());

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ExamTransfer-B04-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class RecordingFolderDialog(string? folder) : IFolderDialogService
    {
        public int PickCount { get; private set; }
        public string? PickFolder()
        {
            PickCount++;
            return folder;
        }
    }

    private sealed class SilentRealtimeService : IRealtimeService
    {
        public bool IsConnected { get; private set; }
        public event EventHandler<string>? EventReceived;
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived { add { } remove { } }
        public Task ConnectAsync(string? token = null, CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }
        public Task SubscribeSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnsubscribeSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) { IsConnected = false; EventReceived?.Invoke(this, "Disconnected"); return Task.CompletedTask; }
    }

    private sealed record DownloadCall(
        Guid SubmissionId,
        Guid FileId,
        string Destination,
        string ExpectedSha256);

    private sealed class RecordingBackendClient : IBackendClient
    {
        private readonly SessionSummaryDto session = MakeSession();
        public IReadOnlyList<SubmissionSummaryDto> Submissions { get; init; } = [];
        public HashSet<Guid> FailedFileIds { get; init; } = [];
        public Func<Guid, string, CancellationToken, Task>? OnDownload { get; init; }
        public List<DownloadCall> Downloads { get; } = [];
        public Uri BaseAddress { get; } = new("http://localhost:5048/");
        public bool HasTrustedAccountToken => true;
        public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error) { error = null; return true; }
        public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(ApiResponse<PagedResult<SessionSummaryDto>>.Ok(new([session], 1, 50, 1), "test"));
        public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(ApiResponse<PagedResult<SubmissionSummaryDto>>.Ok(new(Submissions, 1, 50, Submissions.Count), "test"));
        public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var segments = path.Split('/');
            var submissionId = Guid.Parse(segments[3]);
            var fileId = Guid.Parse(segments[5]);
            Downloads.Add(new(submissionId, fileId, destinationPath, string.Empty));
            if (FailedFileIds.Contains(fileId)) throw new IOException("simulated download failure");
            return OnDownload?.Invoke(fileId, destinationPath, ct) ?? Task.CompletedTask;
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
        public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var segments = path.Split('/');
            var submissionId = Guid.Parse(segments[3]);
            var fileId = Guid.Parse(segments[5]);
            Downloads.Add(new(submissionId, fileId, destinationPath, expectedSha256));
            if (FailedFileIds.Contains(fileId)) throw new IOException("simulated download failure");
            return OnDownload?.Invoke(fileId, destinationPath, ct) ?? Task.CompletedTask;
        }
        public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public void SetBearerToken(string? token) { }
        public void SetAccountToken(string? token) { }
        public void SetParticipantToken(string? token) { }
    }
}
