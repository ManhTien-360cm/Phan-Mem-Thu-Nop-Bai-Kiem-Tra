using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SixFindingsV133Tests
{
    private const string PublishableKey = "sb_publishable_12345678901234567890";

    [Fact]
    public void InstalledPublicConfig_IsSharedValidatedAndEnvironmentCanOverride()
    {
        var directory = Path.Combine(Path.GetTempPath(), "examtransfer-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, PublicCloudRuntimeOptionsProvider.ConfigFileName);
            File.WriteAllText(
                path,
                $$"""{"supabaseUrl":"https://installed.supabase.co","publishableKey":"{{PublishableKey}}"}""");
            var environment = new Dictionary<string, string?>();
            var provider = new PublicCloudRuntimeOptionsProvider(
                path,
                name => environment.GetValueOrDefault(name));

            var installed = provider.Get();
            Assert.True(installed.Configured);
            Assert.Equal("InstalledFile", installed.Source);
            Assert.Equal("installed.supabase.co", installed.ProjectUri!.Host);

            environment["EXAMTRANSFER_SUPABASE_URL"] = "https://override.supabase.co";
            environment["EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY"] = PublishableKey;
            var overridden = provider.Get();
            Assert.True(overridden.Configured);
            Assert.Equal("Environment", overridden.Source);
            Assert.Equal("override.supabase.co", overridden.ProjectUri!.Host);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("sb_secret_forbidden")]
    [InlineData("service_role")]
    [InlineData("placeholder")]
    public void PublicConfig_RejectsSecretOrPlaceholderKeys(string key)
    {
        var options = PublicCloudRuntimeOptionsProvider.Validate(
            "https://project.supabase.co",
            key,
            "test");

        Assert.False(options.Configured);
        Assert.Equal("PUBLICCLOUD_INVALID_PUBLISHABLE_KEY", options.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentForcedRefresh_UsesOneRefreshAndRotatesToken()
    {
        var handler = new RefreshGateHandler();
        var client = Client(handler);
        await client.LoginAsync("student", "password", default);

        var refreshes = Enumerable.Range(0, 6)
            .Select(_ => client.GetValidAccessTokenAsync(true, default))
            .ToArray();
        await WaitUntilAsync(() => handler.RefreshCalls == 1);
        handler.ReleaseRefresh();
        var tokens = await Task.WhenAll(refreshes);

        Assert.All(tokens, token => Assert.Equal("new-access-token", token));
        Assert.Equal(1, handler.RefreshCalls);
        Assert.DoesNotContain("old-access-token", handler.RequestDiagnostics);
        Assert.DoesNotContain("new-refresh-token", handler.RequestDiagnostics);
    }

    [Fact]
    public async Task PublicJoin_RetriesOnlyProjectionNotFoundAndStopsBoundedly()
    {
        var handler = new ProjectionJoinHandler(projectionFailures: 2);
        var delays = new List<TimeSpan>();
        var client = Client(
            handler,
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });
        await client.LoginAsync("student", "password", default);

        var result = await client.JoinByRoomCodeAsync(
            "ROOM42",
            "device",
            "machine",
            "1.3.3",
            default);

        Assert.Equal(3, handler.JoinCalls);
        Assert.Equal(2, delays.Count);
        Assert.Equal(ParticipantStatus.PendingApproval, result.ParticipantStatus);

        var capacityHandler = new ProjectionJoinHandler(
            projectionFailures: 0,
            terminalCode: "PUBLIC_SESSION_CAPACITY_REACHED");
        var capacityClient = Client(capacityHandler, (_, _) => Task.CompletedTask);
        await capacityClient.LoginAsync("student", "password", default);
        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            capacityClient.JoinByRoomCodeAsync(
                "ROOM42",
                "device",
                "machine",
                "1.3.3",
                default));
        Assert.Equal("PUBLIC_SESSION_CAPACITY_REACHED", error.Code);
        Assert.Equal(1, capacityHandler.JoinCalls);
    }

    [Fact]
    public async Task PublicRealtimeLifetime_IsNotLinkedToJoinPageToken_AndSameSessionIsIdempotent()
    {
        var options = new FixedPublicCloudRuntimeOptionsProvider(
            PublicCloudRuntimeOptionsProvider.Validate(
                "http://127.0.0.1:1",
                PublishableKey,
                "test",
                allowLoopbackHttp: true));
        var publicClient = new SupabasePublicCloudClient(
            new HttpClient(new LoginOnlyHandler()),
            optionsProvider: options);
        await publicClient.LoginAsync("student", "password", default);
        var publicRealtime = new SupabaseRealtimeService(options);
        var state = new StudentSessionState
        {
            SessionId = Guid.NewGuid(),
            ParticipantId = Guid.NewGuid(),
            AccessMode = SessionAccessMode.PublicCloud,
            AccessToken = "participant-placeholder"
        };
        using var runtime = new StudentRealtimeService(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            state,
            publicRealtime,
            publicClient);
        using var joinPageLifetime = new CancellationTokenSource();

        await runtime.StartAsync(joinPageLifetime.Token);
        joinPageLifetime.Cancel();
        Assert.True(runtime.IsRunning);
        Assert.Equal(state.SessionId, runtime.ActiveSessionId);

        await runtime.StartAsync(CancellationToken.None);
        Assert.True(runtime.IsRunning);
        Assert.Equal(state.SessionId, runtime.ActiveSessionId);

        await runtime.StopAsync();
        Assert.False(runtime.IsRunning);
        Assert.Null(runtime.ActiveSessionId);
    }

    [Fact]
    public async Task WaitingRealtimeBurst_CoalescesAndIgnoresForeignSession()
    {
        var state = WaitingState();
        using var realtime = new ControllableRealtime();
        var delay = new ControllableDelay();
        var flow = new SequencedFlow(
            state,
            Pending(),
            ReadyFile());
        using var viewModel = WaitingViewModel(state, realtime, flow, delay);
        await viewModel.InitializeAsync(default);

        realtime.Raise(new(
            Guid.NewGuid(),
            RealtimeEvents.SessionStateChanged,
            2,
            null));
        Assert.Equal(1, flow.ResolveCalls);

        for (var i = 0; i < 5; i++)
            realtime.Raise(new(
                state.SessionId!.Value,
                RealtimeEvents.SessionStateChanged,
                i + 2,
                null));
        delay.ReleaseLatestDebounce();
        await WaitUntilAsync(() => flow.ResolveCalls == 2);

        Assert.Equal(1, flow.NavigationCount);
        Assert.Equal(2, flow.ResolveCalls);
    }

    [Fact]
    public async Task WaitingSameRevisionRepeatedThreeTimes_ResolvesAndNavigatesOnce()
    {
        var state = WaitingState();
        using var realtime = new ControllableRealtime();
        var delay = new ControllableDelay();
        var flow = new SequencedFlow(state, Pending(), ReadyFile());
        using var viewModel = WaitingViewModel(state, realtime, flow, delay);
        await viewModel.InitializeAsync(default);

        for (var index = 0; index < 3; index++)
            realtime.Raise(new(
                state.SessionId!.Value,
                RealtimeEvents.SessionStateChanged,
                2,
                null));
        delay.ReleaseLatestDebounce();
        await WaitUntilAsync(() => flow.ResolveCalls == 2);

        Assert.Equal(2, flow.ResolveCalls);
        Assert.Equal(1, flow.NavigationCount);
    }

    [Fact]
    public async Task WaitingPollingAndManualRefresh_ReResolveThroughCoordinator()
    {
        var pollingState = WaitingState();
        using var pollingRealtime = new ControllableRealtime();
        var pollingDelay = new ControllableDelay();
        var pollingFlow = new SequencedFlow(pollingState, Pending(), ReadyFile());
        using var pollingViewModel = WaitingViewModel(
            pollingState,
            pollingRealtime,
            pollingFlow,
            pollingDelay);
        await pollingViewModel.InitializeAsync(default);
        pollingDelay.ReleaseLatestPoll();
        await WaitUntilAsync(() => pollingFlow.NavigationCount == 1);
        Assert.Equal(1, pollingFlow.NavigationCount);

        var manualState = WaitingState();
        using var manualRealtime = new ControllableRealtime();
        var manualDelay = new ControllableDelay();
        var manualFlow = new SequencedFlow(manualState, Pending(), ReadyFile());
        using var manualViewModel = WaitingViewModel(
            manualState,
            manualRealtime,
            manualFlow,
            manualDelay);
        await manualViewModel.InitializeAsync(default);
        manualViewModel.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => manualFlow.NavigationCount == 1);
        Assert.Equal(1, manualFlow.NavigationCount);
    }

    [Fact]
    public async Task WaitingDisposeAndTerminalState_StopCallbacksAndRuntime()
    {
        var disposedState = WaitingState();
        using var disposedRealtime = new ControllableRealtime();
        var disposedDelay = new ControllableDelay();
        var disposedFlow = new SequencedFlow(disposedState, Pending(), ReadyFile());
        var disposedViewModel = WaitingViewModel(
            disposedState,
            disposedRealtime,
            disposedFlow,
            disposedDelay);
        await disposedViewModel.InitializeAsync(default);
        disposedViewModel.Dispose();
        disposedRealtime.Raise(new(
            disposedState.SessionId!.Value,
            RealtimeEvents.SessionStateChanged,
            2,
            null));
        Assert.Equal(1, disposedFlow.ResolveCalls);
        Assert.Equal(0, disposedRealtime.SubscriberCount);

        var terminalState = WaitingState();
        using var terminalRealtime = new ControllableRealtime();
        var terminalDelay = new ControllableDelay();
        var terminalFlow = new SequencedFlow(terminalState, Pending(), Rejected());
        using var terminalViewModel = WaitingViewModel(
            terminalState,
            terminalRealtime,
            terminalFlow,
            terminalDelay);
        await terminalViewModel.InitializeAsync(default);
        terminalRealtime.Raise(new(
            terminalState.SessionId!.Value,
            "ParticipantRejected",
            2,
            null,
            terminalState.ParticipantId));
        terminalDelay.ReleaseLatestDebounce();
        await WaitUntilAsync(() => terminalRealtime.StopCalls == 1);
        Assert.Equal(1, terminalRealtime.StopCalls);
    }

    private static StudentWaitingViewModel WaitingViewModel(
        StudentSessionState state,
        ControllableRealtime realtime,
        SequencedFlow flow,
        ControllableDelay delay) =>
        new(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            state,
            new AppAuthSessionState(),
            realtime,
            flow,
            delay.DelayAsync,
            TimeSpan.FromSeconds(10),
            3);

    private static StudentSessionState WaitingState() => new()
    {
        SessionId = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        AccessMode = SessionAccessMode.LanOnly,
        AccessToken = "participant-token",
        RoomCode = "ROOM42",
        StudentCode = "SV001",
        DisplayName = "Student",
        ParticipantStatus = ParticipantStatus.PendingApproval,
        SessionStatus = SessionStatus.Waiting
    };

    private static StudentExamFlowResolution Pending() => new(
        StudentExamFlowState.PendingApproval,
        "S-03",
        false,
        "pending");

    private static StudentExamFlowResolution ReadyFile() => new(
        StudentExamFlowState.ReadyToStartFileExam,
        "S-05",
        false,
        "ready");

    private static StudentExamFlowResolution Rejected() => new(
        StudentExamFlowState.RejectedOrExpired,
        "S-01",
        false,
        "rejected");

    private static SupabasePublicCloudClient Client(
        HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            new HttpClient(handler),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key",
            delay: delay);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class RefreshGateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshCalls;
        public string RequestDiagnostics => $"refresh_calls={RefreshCalls}";

        public void ReleaseRefresh() => release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Query.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref RefreshCalls);
                await release.Task.WaitAsync(cancellationToken);
                return Json("""{"access_token":"new-access-token","refresh_token":"new-refresh-token","expires_in":3600}""");
            }
            return Json("""{"access_token":"old-access-token","refresh_token":"old-refresh-token","expires_in":3600}""");
        }
    }

    private sealed class ProjectionJoinHandler(
        int projectionFailures,
        string? terminalCode = null) : HttpMessageHandler
    {
        public int JoinCalls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v1/token", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"access_token":"token","refresh_token":"refresh","expires_in":3600}"""));
            if (path.EndsWith("/rpc/get_examtransfer_cloud_capabilities", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"schemaVersion":26,"criticalRpcs":["get_public_student_notification_events","send_public_teacher_message","get_student_results"]}"""));

            JoinCalls++;
            if (terminalCode is not null)
                return Task.FromResult(Error(terminalCode));
            if (JoinCalls <= projectionFailures)
                return Task.FromResult(Error("OPEN_PUBLIC_SESSION_NOT_FOUND"));
            return Task.FromResult(Json($$"""
                {
                  "sessionId":"{{Guid.NewGuid()}}",
                  "examId":"{{Guid.NewGuid()}}",
                  "participantId":"{{Guid.NewGuid()}}",
                  "participantStatus":"PendingApproval",
                  "sessionStatus":"Waiting",
                  "roomCode":"ROOM42",
                  "examTitle":"Cloud exam",
                  "subject":"Tin",
                  "durationMinutes":45,
                  "deliveryType":"FileSubmission",
                  "supervisionMode":"None",
                  "quizResultPolicy":"Hidden",
                  "plannedStartUtc":null,
                  "capacity":40,
                  "currentParticipantCount":1
                }
                """));
        }
    }

    private sealed class LoginOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Json(
                """{"access_token":"current-token","refresh_token":"refresh-token","expires_in":3600}"""));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Error(string code) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            $$"""{"code":"P0002","message":"{{code}}"}""",
            Encoding.UTF8,
            "application/json")
    };

    private sealed class ControllableRealtime : IStudentRealtimeService
    {
        private EventHandler<string>? events;
        private EventHandler<StudentRealtimeNotification>? notifications;
        public bool IsConnected => true;
        public bool IsRunning => true;
        public Guid? ActiveSessionId { get; set; }
        public int StopCalls { get; private set; }
        public int SubscriberCount =>
            (events?.GetInvocationList().Length ?? 0)
            + (notifications?.GetInvocationList().Length ?? 0);
        public event EventHandler<string>? EventReceived
        {
            add => events += value;
            remove => events -= value;
        }
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived
        {
            add => notifications += value;
            remove => notifications -= value;
        }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
        public void Raise(StudentRealtimeNotification notification) =>
            notifications?.Invoke(this, notification);
        public void Dispose()
        {
            events = null;
            notifications = null;
        }
    }

    private sealed class SequencedFlow(
        StudentSessionState state,
        params StudentExamFlowResolution[] resolutions) : IStudentExamFlowCoordinator
    {
        private readonly ConcurrentQueue<StudentExamFlowResolution> queue = new(resolutions);
        public int ResolveCalls { get; private set; }
        public int NavigationCount { get; private set; }
        public event EventHandler<StudentExamNavigationRequest>? NavigationRequested;

        public Task<StudentExamFlowResolution> ResolveAsync(
            StudentExamEntryPoint entryPoint,
            bool startConfirmed,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            Assert.True(queue.TryDequeue(out var resolution));
            state.Revision++;
            switch (resolution!.State)
            {
                case StudentExamFlowState.PendingApproval:
                    state.ParticipantStatus = ParticipantStatus.PendingApproval;
                    state.SessionStatus = SessionStatus.Waiting;
                    break;
                case StudentExamFlowState.RejectedOrExpired:
                    state.ParticipantStatus = ParticipantStatus.Rejected;
                    break;
                default:
                    state.ParticipantStatus = ParticipantStatus.Approved;
                    state.SessionStatus = SessionStatus.InProgress;
                    break;
            }
            if (resolution.RouteKey != "S-03" && !resolution.RequiresStartConfirmation)
            {
                NavigationCount++;
                NavigationRequested?.Invoke(this, new(entryPoint, resolution));
            }
            return Task.FromResult(resolution);
        }

        public void NavigateResolved(
            StudentExamEntryPoint entryPoint,
            StudentExamFlowResolution resolution)
        {
            NavigationCount++;
            NavigationRequested?.Invoke(this, new(entryPoint, resolution));
        }

        public Task<StudentJoinOutcome> SynchronizeAfterJoinAsync(
            IStudentRealtimeService realtime,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StudentJoinOutcome(
                StudentJoinErrorCodes.Succeeded,
                StudentJoinPhase.Completed,
                true));

        public void ReturnToCurrentExam() { }
    }

    private sealed class ControllableDelay
    {
        private readonly object sync = new();
        private readonly List<PendingDelay> debounce = [];
        private readonly List<PendingDelay> polls = [];

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(cancellationToken);
            lock (sync)
            {
                (duration < TimeSpan.FromSeconds(1) ? debounce : polls).Add(pending);
            }
            return pending.Task;
        }

        public void ReleaseLatestDebounce() => ReleaseLatest(debounce);
        public void ReleaseLatestPoll() => ReleaseLatest(polls);

        private void ReleaseLatest(List<PendingDelay> values)
        {
            PendingDelay? pending;
            lock (sync)
                pending = values.LastOrDefault(value => !value.Task.IsCompleted);
            Assert.NotNull(pending);
            pending!.Release();
        }

        private sealed class PendingDelay
        {
            private readonly TaskCompletionSource source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public PendingDelay(CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            }
            public Task Task => source.Task;
            public void Release() => source.TrySetResult();
        }
    }
}
