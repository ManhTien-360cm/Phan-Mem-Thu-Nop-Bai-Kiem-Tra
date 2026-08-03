using System.Net;
using System.Net.Http;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudRoomJoinTests
{
    [Fact]
    public async Task ValidRoomCode_UsesCanonicalOpenJoinRpcAndReturnsPendingWaiting()
    {
        var handler = new PublicCloudJoinHandler();
        var client = new SupabasePublicCloudClient(
            new HttpClient(handler),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key");
        await client.LoginAsync("student01", "password", default);

        var result = await client.JoinByRoomCodeAsync(
            "ROOM42",
            "device-1",
            "Student",
            "1.3.1",
            default);

        Assert.Contains(
            "/rest/v1/rpc/join_open_public_session_by_room_code",
            handler.Paths);
        Assert.DoesNotContain(handler.Paths, x => x.Contains("join_public_session/", StringComparison.Ordinal));
        Assert.Equal(ParticipantStatus.PendingApproval, result.ParticipantStatus);
        Assert.Equal(SessionStatus.Waiting, result.SessionStatus);
        Assert.Equal("ROOM42", result.RoomCode);
    }

    [Theory]
    [InlineData("P0003")]
    [InlineData("OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS")]
    public void AmbiguousRoomCode_ShowsActionableErrorWithoutMutatingStudentState(string errorCode)
    {
        var auth = StudentAuth();
        var originalProfile = auth.CurrentAccount;
        var state = new StudentSessionState { AccessMode = SessionAccessMode.PublicCloud };
        var joinCalls = 0;
        using var viewModel = new StudentConnectViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            auth,
            new EmptyDiscovery(),
            _ => throw new InvalidOperationException("LAN path must not run."),
            () => true,
            (_, _) =>
            {
                joinCalls++;
                throw new PublicCloudApiException(
                    errorCode,
                    "ambiguous public room code",
                    HttpStatusCode.BadRequest);
            },
            (_, _) => throw new InvalidOperationException("Post-join completion must not run."));
        viewModel.SelectedAccessMode = SessionAccessMode.PublicCloud;
        viewModel.RoomCode = "ROOM42";

        try
        {
            Assert.True(viewModel.JoinCommand.CanExecute(null));
            viewModel.JoinCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => !viewModel.IsBusy && joinCalls == 1,
                TimeSpan.FromSeconds(3)));

            Assert.Contains(
                "Mã phòng đang bị trùng trên hệ thống.\n"
                + "Giáo viên cần đóng phòng cũ hoặc tạo mã phòng mới.",
                viewModel.Status,
                StringComparison.Ordinal);
            Assert.Contains(errorCode, viewModel.Status, StringComparison.Ordinal);
            Assert.Equal("danger", viewModel.StatusTone);
            Assert.Equal(SessionAccessMode.PublicCloud, viewModel.SelectedAccessMode);
            Assert.Equal(SessionAccessMode.PublicCloud, state.AccessMode);
            Assert.Same(originalProfile, auth.CurrentAccount);
            Assert.Equal(originalProfile!.DisplayName, viewModel.DisplayName);
            Assert.Equal(originalProfile.StudentCode, viewModel.StudentCode);
            Assert.Null(state.SessionId);
            Assert.Null(state.ParticipantId);
            Assert.False(state.JoinMutationCommitted);
            Assert.False(state.PostJoinSynchronizationPending);
            Assert.Equal(StudentJoinErrorCodes.JoinMutationFailed, viewModel.LastJoinOutcome?.Code);
            Assert.Equal(errorCode, viewModel.LastJoinOutcome?.CauseCode);
            Assert.False(viewModel.LastJoinOutcome?.AuthorityMutationCommitted);
            Assert.Equal(1, joinCalls);
        }
        finally
        {
            auth.Clear();
        }
    }

    [Theory]
    [InlineData(
        "P0003",
        "Mã phòng đang bị trùng trên hệ thống.\nGiáo viên cần đóng phòng cũ hoặc tạo mã phòng mới.",
        StudentJoinErrorCodes.JoinMutationFailed)]
    [InlineData(
        "OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS",
        "Mã phòng đang bị trùng trên hệ thống.\nGiáo viên cần đóng phòng cũ hoặc tạo mã phòng mới.",
        StudentJoinErrorCodes.JoinMutationFailed)]
    [InlineData(
        "OPEN_PUBLIC_SESSION_NOT_FOUND",
        "Không tìm thấy phòng PublicCloud. Hãy kiểm tra mã phòng và thử lại.",
        StudentJoinErrorCodes.RoomNotFound)]
    [InlineData(
        "PUBLICCLOUD_AUTH_EXPIRED",
        "Phiên đăng nhập PublicCloud đã hết hạn; hãy đăng nhập lại.",
        StudentJoinErrorCodes.AuthenticationRequired)]
    public void MapPublicJoinError_MapsApiCodeExplicitly(
        string errorCode,
        string expectedMessage,
        string expectedTypedCode)
    {
        var result = StudentConnectViewModel.MapPublicJoinError(
            new PublicCloudApiException(
                errorCode,
                "simulated PublicCloud error",
                HttpStatusCode.BadRequest));

        Assert.Equal(errorCode, result.Code);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(expectedTypedCode, result.TypedCode);
    }

    [Fact]
    public void MapPublicJoinError_MapsNetworkUnavailable()
    {
        var result = StudentConnectViewModel.MapPublicJoinError(
            new HttpRequestException("network unavailable"));

        Assert.Equal("PUBLICCLOUD_NETWORK_UNAVAILABLE", result.Code);
        Assert.Contains("kết nối mạng", result.Message, StringComparison.Ordinal);
        Assert.Equal(StudentJoinErrorCodes.JoinMutationFailed, result.TypedCode);
    }

    [Fact]
    public void MapPublicJoinError_MapsTimeout()
    {
        var result = StudentConnectViewModel.MapPublicJoinError(
            new TaskCanceledException("request timed out"));

        Assert.Equal("PUBLICCLOUD_TIMEOUT", result.Code);
        Assert.Contains("thời gian cho phép", result.Message, StringComparison.Ordinal);
        Assert.Equal(StudentJoinErrorCodes.JoinMutationFailed, result.TypedCode);
    }

    [Fact]
    public void MapPublicJoinError_MapsUnknownFailure()
    {
        var result = StudentConnectViewModel.MapPublicJoinError(
            new InvalidOperationException("unexpected failure"));

        Assert.Equal("PUBLICCLOUD_UNKNOWN_ERROR", result.Code);
        Assert.Equal("Không thể hoàn tất yêu cầu PublicCloud.", result.Message);
        Assert.Equal(StudentJoinErrorCodes.JoinMutationFailed, result.TypedCode);
    }

    private static AppAuthSessionState StudentAuth()
    {
        var auth = new AppAuthSessionState();
        auth.SetAuthenticated(
            new CurrentAccountDto(
                Guid.NewGuid(),
                "student01",
                null,
                "Học sinh",
                "HS001",
                UserRole.Student,
                null,
                Guid.NewGuid(),
                "device-1",
                DateTimeOffset.UtcNow.AddHours(1),
                new DateOnly(2010, 1, 1)),
            "account-token");
        return auth;
    }

    private sealed class EmptyDiscovery : ILanDiscoveryService
    {
        public Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
            TimeSpan timeout,
            string? roomCode = null,
            CancellationToken ct = default) =>
            Task.FromResult(new LanDiscoverySnapshot([], [], "test", 0));

        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoveryServerDto>>([]);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenSessionDiscoveryDto>>([]);

        public Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
            string roomCode,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<OpenSessionDiscoveryDto?>(null);
    }

    private sealed class PublicCloudJoinHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var json = request.RequestUri.AbsolutePath.EndsWith("/auth/v1/token", StringComparison.Ordinal)
                ? """{"access_token":"cloud-token","refresh_token":"refresh-token","expires_in":3600}"""
                : request.RequestUri.AbsolutePath.EndsWith(
                    "/rpc/get_examtransfer_cloud_capabilities",
                    StringComparison.Ordinal)
                    ? """{"schemaVersion":26,"criticalRpcs":["get_public_student_notification_events","send_public_teacher_message","get_student_results"]}"""
                    : $$"""
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
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
