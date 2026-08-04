using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentResultsTests
{
    [Fact]
    public async Task EmptyListShowsClearEmptyStateAndLoadingFinishes()
    {
        using var context = TestContext.Create();
        var pending = context.Results.DelayNext();
        using var viewModel = context.CreateViewModel();

        var initialization = viewModel.InitializeAsync(CancellationToken.None);
        await pending.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsLoading);
        pending.Complete([]);
        await initialization;

        Assert.Empty(viewModel.Results);
        Assert.True(viewModel.HasNoResults);
        Assert.Equal("Chưa có kết quả nào được giáo viên trả.", viewModel.EmptyStateText);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task QuizResultShowsScoreCommentAndQuestionOutcomes()
    {
        using var context = TestContext.Create(ReturnedQuiz());
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        var result = Assert.Single(viewModel.Results);
        Assert.Equal("Bài trắc nghiệm", result.TypeText);
        Assert.Equal("8/10", result.ScoreText);
        Assert.Equal("Tốt", result.CommentText);
        Assert.Collection(result.Questions,
            row => Assert.Equal("Đúng", row.OutcomeText),
            row => Assert.Equal("Sai", row.OutcomeText),
            row => Assert.Equal("Bỏ trống", row.OutcomeText));
    }

    [Fact]
    public async Task EssayResultShowsReturnedTimeAndDownloadsAttachment()
    {
        var essay = ReturnedEssay();
        using var context = TestContext.Create(essay);
        using var destination = new TemporaryDirectory();
        context.Folders.Folder = destination.Path;
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedResult = Assert.Single(viewModel.Results);
        viewModel.SelectedAttachment = Assert.Single(viewModel.SelectedResult.Attachments);
        Assert.True(viewModel.DownloadAttachmentCommand.CanExecute(null));
        viewModel.DownloadAttachmentCommand.Execute(null);
        await WaitUntilAsync(() => context.Results.Downloads.Count == 1 && !viewModel.IsBusy);

        var download = Assert.Single(context.Results.Downloads);
        Assert.Equal(essay.ResultId, download.ResultId);
        Assert.Equal(essay.Attachments[0].Id, download.AttachmentId);
        Assert.StartsWith(destination.Path, download.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Không có dữ liệu", viewModel.SelectedResult.ReturnedAtText);
    }

    [Fact]
    public async Task DraftAndGradedResultsAreNeverDisplayed()
    {
        var returned = ReturnedEssay();
        var draft = returned with { ResultId = Guid.NewGuid(), Status = GradingStatus.NotGraded };
        var graded = returned with { ResultId = Guid.NewGuid(), Status = GradingStatus.Graded };
        using var context = TestContext.Create(draft, graded, returned);
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(returned.ResultId, Assert.Single(viewModel.Results).ResultId);
    }

    [Fact]
    public async Task MatchingReturnedAndReopenedEventsRefreshButWrongParticipantDoesNot()
    {
        using var context = TestContext.Create(ReturnedQuiz());
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);
        var initialCalls = context.Results.LoadCalls;

        context.Realtime.Publish(new StudentRealtimeNotification(
            context.Session.SessionId!.Value,
            RealtimeEvents.GradeReturned,
            2,
            null,
            context.Session.ParticipantId));
        await WaitUntilAsync(() => context.Results.LoadCalls == initialCalls + 1);

        context.Realtime.Publish(new StudentRealtimeNotification(
            context.Session.SessionId.Value,
            "GradeReopened",
            3,
            null,
            context.Session.ParticipantId));
        await WaitUntilAsync(() => context.Results.LoadCalls == initialCalls + 2);

        context.Realtime.Publish(new StudentRealtimeNotification(
            context.Session.SessionId.Value,
            RealtimeEvents.QuizGradeReturned,
            4,
            null,
            Guid.NewGuid()));
        await Task.Delay(50);
        Assert.Equal(initialCalls + 2, context.Results.LoadCalls);
    }

    [Fact]
    public async Task PublicCloudAndOnlyLanResultsUseSamePresentationWithoutStudentIdInput()
    {
        var cloud = ReturnedQuiz() with { SourceMode = SessionAccessMode.PublicCloud };
        var lan = ReturnedEssay() with { SourceMode = SessionAccessMode.LanOnly };
        using var context = TestContext.Create(cloud, lan);
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Contains(viewModel.Results, result => result.SourceText == "PublicCloud");
        Assert.Contains(viewModel.Results, result => result.SourceText == "OnlyLAN");
        Assert.All(viewModel.Results, result => Assert.Equal("Đã trả", result.StatusText));
    }

    [Fact]
    public async Task OnlyLanServiceConsumesUnifiedA05ContractAndBuildsAttachmentRouteFromIds()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var backend = new UnifiedResultsBackendClient(new StudentResultPageDto
        {
            Items =
            [
                new StudentResultDto
                {
                    ResultType = StudentResultType.EssayFile,
                    ExamId = Guid.NewGuid(),
                    ExamTitle = "Essay",
                    SessionId = sessionId,
                    SubmissionId = submissionId,
                    AttemptNumber = 2,
                    Status = StudentResultStatus.Returned,
                    Score = 8.5m,
                    MaxScore = 10m,
                    ReturnedAtUtc = DateTimeOffset.UtcNow,
                    Attachments =
                    [
                        new StudentResultAttachmentDto
                        {
                            AttachmentId = attachmentId,
                            FileName = "feedback.pdf",
                            ContentType = "application/pdf",
                            SizeBytes = 123
                        }
                    ]
                }
            ]
        });
        var session = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly
        };
        var service = new StudentResultsService(
            backend,
            new SupabasePublicCloudClient(),
            session);

        var result = Assert.Single(await service.GetReturnedResultsAsync(CancellationToken.None));

        Assert.Equal("api/v1/student/results?pageSize=50", backend.RequestedPath);
        Assert.Equal(submissionId, result.ResultId);
        Assert.Equal(participantId, result.ParticipantId);
        Assert.Equal(2, result.AttemptNumber);
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal(
            $"api/v1/student/submissions/{submissionId}/grade/attachments/{attachmentId}/content",
            attachment.DownloadPath);
        Assert.DoesNotContain("private", attachment.DownloadPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicCloudClientUsesActorScopedResultsRpcAndA05PageContract()
    {
        var attemptId = Guid.NewGuid();
        var handler = new StudentResultsCloudHandler(JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    resultType = "Quiz",
                    examId = Guid.NewGuid(),
                    examTitle = "Cloud quiz",
                    sessionId = Guid.NewGuid(),
                    submissionId = (Guid?)null,
                    attemptId,
                    attemptNumber = 4,
                    status = "Returned",
                    score = 7.5m,
                    maxScore = 10m,
                    generalComment = "Returned",
                    returnedAtUtc = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero),
                    attachments = Array.Empty<object>(),
                    quizSummary = new
                    {
                        totalQuestions = 4,
                        answeredQuestions = 3,
                        correctCount = 3,
                        incorrectCount = 0,
                        unansweredCount = 1,
                        earnedPoints = 7.5m,
                        maxPoints = 10m
                    }
                }
            },
            nextCursor = (object?)null
        }));
        using var http = new HttpClient(handler);
        var client = new SupabasePublicCloudClient(
            http,
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-key");
        await client.LoginAsync("student", "password", CancellationToken.None);

        var page = await client.GetStudentResultsAsync(25, null, CancellationToken.None);

        var result = Assert.Single(page.Items);
        Assert.Equal(attemptId, result.AttemptId);
        Assert.Equal(StudentResultType.Quiz, result.ResultType);
        Assert.Equal(4, result.AttemptNumber);
        Assert.Equal("/rest/v1/rpc/get_student_results", handler.RpcPath);
        Assert.Contains("\"p_page_size\":25", handler.RpcBody, StringComparison.Ordinal);
        Assert.DoesNotContain("student", handler.RpcBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("participant", handler.RpcBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogoutClearsResultsAndAccountSwitchRejectsStaleResponse()
    {
        using var context = TestContext.Create(ReturnedEssay(title: "Tài khoản A"));
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Single(viewModel.Results);

        var delayed = context.Results.DelayNext();
        viewModel.RetryCommand.Execute(null);
        await delayed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        context.Results.DefaultResults = [ReturnedQuiz(title: "Tài khoản B")];
        context.SignIn(Guid.NewGuid());
        await WaitUntilAsync(() => viewModel.Results.SingleOrDefault()?.Title == "Tài khoản B");
        delayed.Complete([ReturnedEssay(title: "Phản hồi cũ")]);
        await Task.Delay(50);
        Assert.Equal("Tài khoản B", Assert.Single(viewModel.Results).Title);

        context.Auth.Clear();
        Assert.Empty(viewModel.Results);
        Assert.True(viewModel.HasNoResults);
    }

    [Fact]
    public async Task ErrorStateCanRetrySuccessfully()
    {
        using var context = TestContext.Create(ReturnedEssay());
        context.Results.FailuresRemaining = 1;
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.HasError);
        Assert.NotEmpty(viewModel.ErrorMessage);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
        viewModel.RetryCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasError && viewModel.Results.Count == 1 && !viewModel.IsBusy);
    }

    [Fact]
    public void NavigationViewAndIntegrationRequirementsAreWired()
    {
        var main = File.ReadAllText(FindFile("frontend", "src", "ExamTransfer.Desktop", "ViewModels", "MainViewModel.cs"));
        var window = File.ReadAllText(FindFile("frontend", "src", "ExamTransfer.Desktop", "Views", "MainWindow.xaml"));
        var view = File.ReadAllText(FindFile("frontend", "src", "ExamTransfer.Desktop", "Views", "StudentResultsView.xaml"));
        var requirements = File.ReadAllText(FindFile("B_INTEGRATION_REQUIREMENTS.md"));

        Assert.Contains("S-11", main, StringComparison.Ordinal);
        Assert.Contains("new StudentResultsViewModel", main, StringComparison.Ordinal);
        Assert.Contains("StudentResultsViewModel", window, StringComparison.Ordinal);
        Assert.Contains("StudentResultsView", window, StringComparison.Ordinal);
        Assert.Contains("DownloadAttachmentCommand", view, StringComparison.Ordinal);
        Assert.Contains("## B-09", requirements, StringComparison.Ordinal);
        Assert.Contains("tài khoản đã xác thực", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không nhận hoặc gửi `StudentId`", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ liệt kê kết quả ở trạng thái `Returned`", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReturnedAtUtc", requirements, StringComparison.Ordinal);
        Assert.Contains("participant", requirements, StringComparison.OrdinalIgnoreCase);
    }

    private static StudentReturnedResult ReturnedQuiz(string title = "Quiz")
    {
        var choices = new[]
        {
            new QuizChoiceReviewDto(Guid.NewGuid(), "A", 1, true, true),
            new QuizChoiceReviewDto(Guid.NewGuid(), "B", 2, false, false)
        };
        var wrong = new[]
        {
            new QuizChoiceReviewDto(Guid.NewGuid(), "A", 1, true, false),
            new QuizChoiceReviewDto(Guid.NewGuid(), "B", 2, false, true)
        };
        var blank = new[]
        {
            new QuizChoiceReviewDto(Guid.NewGuid(), "A", 1, false, true)
        };
        return new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), title,
            StudentResultKind.Quiz, 2, GradingStatus.Returned, 8m, 10m, "Tốt",
            DateTimeOffset.UtcNow, SessionAccessMode.PublicCloud,
            [
                new(Guid.NewGuid(), "Đúng", 1, 1m, 1m, choices),
                new(Guid.NewGuid(), "Sai", 2, 1m, 0m, wrong),
                new(Guid.NewGuid(), "Trống", 3, 1m, 0m, blank)
            ], []);
    }

    private static StudentReturnedResult ReturnedEssay(string title = "Tự luận")
    {
        var id = Guid.NewGuid();
        return new(
            id, Guid.NewGuid(), Guid.NewGuid(), title,
            StudentResultKind.EssayFile, 1, GradingStatus.Returned, 7.5m, 10m,
            "Cần trình bày rõ hơn", DateTimeOffset.UtcNow, SessionAccessMode.LanOnly,
            [], [new(Guid.NewGuid(), "nhan-xet.pdf", 100, "application/pdf", $"api/v1/student/submissions/{id}/grade/attachments/file/content")]);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < timeoutAt) await Task.Delay(10);
        Assert.True(predicate());
    }

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string authPath = Path.Combine(Path.GetTempPath(), $"ExamTransfer-B09-{Guid.NewGuid():N}.bin");
        private TestContext(params StudentReturnedResult[] results)
        {
            Auth = new(authPath);
            Results = new(results);
            Session = new()
            {
                SessionId = Guid.NewGuid(),
                ParticipantId = Guid.NewGuid(),
                AccessMode = SessionAccessMode.PublicCloud
            };
            SignIn(Guid.NewGuid());
        }
        public AppAuthSessionState Auth { get; }
        public RecordingStudentResultsService Results { get; }
        public StudentSessionState Session { get; }
        public RecordingRealtimeService Realtime { get; } = new();
        public RecordingFolderDialog Folders { get; } = new();
        public static TestContext Create(params StudentReturnedResult[] results) => new(results);
        public StudentResultsViewModel CreateViewModel() => new(Results, Auth, Session, Realtime, Folders);
        public void SignIn(Guid userId) => Auth.SetAuthenticated(
            new(userId, "student", "student@example.test", "Học sinh", "HS01", UserRole.Student,
                "org", Guid.NewGuid(), "device", DateTimeOffset.UtcNow.AddHours(1)),
            "token-" + userId,
            AuthSessionAuthority.Supabase);
        public void Dispose()
        {
            Auth.Clear();
            if (File.Exists(authPath)) File.Delete(authPath);
        }
    }

    private sealed class DelayedResults
    {
        private readonly TaskCompletionSource<IReadOnlyList<StudentReturnedResult>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<StudentReturnedResult>> WaitAsync() { Requested.TrySetResult(); return await completion.Task; }
        public void Complete(IReadOnlyList<StudentReturnedResult> value) => completion.TrySetResult(value);
    }

    private sealed record DownloadCall(Guid ResultId, Guid AttachmentId, string Destination);

    private sealed class RecordingStudentResultsService(params StudentReturnedResult[] results) : IStudentResultsService
    {
        private DelayedResults? delayed;
        public IReadOnlyList<StudentReturnedResult> DefaultResults { get; set; } = results;
        public int LoadCalls { get; private set; }
        public int FailuresRemaining { get; set; }
        public List<DownloadCall> Downloads { get; } = [];
        public DelayedResults DelayNext() => delayed = new();
        public async Task<IReadOnlyList<StudentReturnedResult>> GetReturnedResultsAsync(CancellationToken cancellationToken)
        {
            LoadCalls++;
            if (FailuresRemaining-- > 0) throw new InvalidOperationException("backend unavailable");
            if (delayed is { } current)
            {
                delayed = null;
                return await current.WaitAsync();
            }
            return DefaultResults;
        }
        public Task DownloadAttachmentAsync(StudentResultAttachment attachment, string destinationPath, CancellationToken cancellationToken)
        {
            Downloads.Add(new(attachment.ResultId, attachment.Id, destinationPath));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRealtimeService : IStudentRealtimeService
    {
        public bool IsConnected => true;
        public event EventHandler<string>? EventReceived;
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived;
        public void Publish(StudentRealtimeNotification value) => NotificationReceived?.Invoke(this, value);
        public void Publish(string value) => EventReceived?.Invoke(this, value);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class RecordingFolderDialog : IFolderDialogService
    {
        public string? Folder { get; set; }
        public string? PickFolder() => Folder;
    }

    private sealed class UnifiedResultsBackendClient(StudentResultPageDto page) : IBackendClient
    {
        public string? RequestedPath { get; private set; }
        public Uri BaseAddress { get; } = new("http://localhost:5048/");
        public bool HasTrustedAccountToken => true;
        public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error) { error = null; return true; }
        public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
        {
            RequestedPath = path;
            return Task.FromResult<ApiResponse<T>?>(ApiResponse<T>.Ok((T)(object)page, "test"));
        }
        public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SystemStatusDto>?>(null);
        public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<DashboardSummaryDto>?>(null);
        public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<ClassSummaryDto>>?>(null);
        public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<ExamSummaryDto>>?>(null);
        public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(null);
        public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ApiResponse<SessionDetailDto>?>(null);
        public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(null);
        public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<CloudSyncStatusDto>?>(null);
        public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SettingsDto>?>(null);
        public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default) => Task.FromResult<ApiResponse<object>?>(null);
        public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public void SetBearerToken(string? token) { }
        public void SetAccountToken(string? token) { }
        public void SetParticipantToken(string? token) { }
    }

    private sealed class StudentResultsCloudHandler(string pageJson) : HttpMessageHandler
    {
        public string? RpcPath { get; private set; }
        public string RpcBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = """{"access_token":"access","refresh_token":"refresh","expires_in":3600}""";
            if (path.EndsWith("/rpc/get_student_results", StringComparison.Ordinal))
            {
                RpcPath = path;
                RpcBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                content = pageJson;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ExamTransfer-B09-files-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
