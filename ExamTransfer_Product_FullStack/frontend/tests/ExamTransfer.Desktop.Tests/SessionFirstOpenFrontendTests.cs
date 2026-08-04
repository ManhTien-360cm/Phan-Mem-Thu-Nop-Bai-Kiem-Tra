using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SessionFirstOpenFrontendTests
{
    [Fact]
    public async Task StudentScan_SeparatesRoomsFromServers_AndSelectionUsesRoomMetadata()
    {
        var room = new OpenSessionDiscoveryDto(
            Guid.NewGuid(),
            "ROOM42",
            "Phòng thi Toán",
            null,
            null,
            null,
            "Kiểm tra đại số",
            "Cô Lan",
            SessionStatus.Waiting,
            true,
            36,
            4,
            null,
            DateTimeOffset.UtcNow.AddMinutes(10),
            SessionAccessMode.LanOnly,
            "server-1",
            "Máy cô Lan",
            "http://10.10.0.8:5048",
            DateTimeOffset.UtcNow,
            "1",
            "Toán",
            45,
            ExamDeliveryType.FileSubmission,
            SupervisionMode.None,
            SessionAdmissionMode.OpenRequest,
            Guid.NewGuid());
        var server = new DiscoveryServerDto(
            "ExamTransfer.Discovery.v1",
            "Máy cô Lan",
            "10.10.0.8",
            5048,
            "fingerprint",
            1,
            "1.0.0",
            DateTimeOffset.UtcNow,
            "server-1");
        var discovery = new StubLanDiscovery([server], [room]);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow);
        using var viewModel = new StudentConnectViewModel(
            api,
            new StudentSessionState(),
            new AppAuthSessionState(),
            discovery);

        await viewModel.InitializeAsync(CancellationToken.None);

        var selected = Assert.Single(viewModel.Rooms);
        Assert.Single(viewModel.Servers);
        Assert.True(viewModel.HasRooms);
        Assert.Equal("Kiểm tra đại số", selected.ExamTitle);
        Assert.Equal("Toán", selected.Subject);
        Assert.Equal("45 phút", selected.DurationText);
        Assert.Equal("Cô Lan", selected.TeacherName);
        Assert.Equal("ROOM42", viewModel.RoomCode);
        Assert.Equal("10.10.0.8", viewModel.Ip);
        Assert.Equal("5048", viewModel.Port);
        Assert.Null(selected.Room.ClassId);
        Assert.Null(selected.Room.ClassCode);
    }

    [Fact]
    public async Task StudentScan_WithOnlyServer_ShowsRoomEmptyState()
    {
        var discovery = new StubLanDiscovery(
            [new(
                "ExamTransfer.Discovery.v1",
                "Máy giáo viên",
                "10.10.0.9",
                5048,
                "fingerprint",
                0,
                "1.0.0",
                DateTimeOffset.UtcNow)],
            []);
        using var viewModel = new StudentConnectViewModel(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            new StudentSessionState(),
            new AppAuthSessionState(),
            discovery);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Empty(viewModel.Rooms);
        Assert.Single(viewModel.Servers);
        Assert.True(viewModel.HasNoRooms);
        Assert.Null(viewModel.SelectedRoom);
    }

    [Fact]
    public async Task TeacherQuickCreate_IsAlwaysOpenClasslessNonAutoApprove()
    {
        var classId = Guid.NewGuid();
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            classId,
            "Kiểm tra",
            "Toán",
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Published,
            1,
            1,
            "exam-rv");
        var summary = new SessionSummaryDto(
            Guid.NewGuid(),
            exam.Id,
            exam.Title,
            "ROOM42",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv",
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [],
            SessionDetailResponse = new(summary, [], "{}", Capacity: 36)
        };
        using var viewModel = new SessionManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.AutoApprove = true;

        Assert.True(viewModel.CreateCommand.CanExecute(null));
        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/sessions/create-and-open"),
            TimeSpan.FromSeconds(2)));
        var quick = Assert.IsType<CreateSessionRequest>(api.PostRequests[0]);
        Assert.Null(quick.ClassId);
        Assert.Equal(SessionAdmissionMode.OpenRequest, quick.AdmissionMode);
        Assert.False(quick.AutoApprove);
        Assert.Equal("{\"autoApprove\":false}", quick.SettingsJson);
    }

    [Fact]
    public async Task PublicCloudRoomConflict_RequiresNewCodeAndStaysUnshareableUntilReady()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Kiểm tra",
            "Toán",
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Published,
            1,
            1,
            "exam-rv");
        var sessionId = Guid.NewGuid();
        var conflicted = new SessionSummaryDto(
            sessionId,
            exam.Id,
            exam.Title,
            "PUB133",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv-1",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [],
            SessionDetailResponse = new(conflicted, [], "{}", Capacity: 36)
        };
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            false,
            SyncStatus.Conflict,
            ErrorCodes.RoomCodeConflict,
            "Mã phòng PublicCloud đang được sử dụng.",
            1));
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 3);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.AccessMode = SessionAccessMode.PublicCloud;

        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => viewModel.CanRecoverRoomCode,
            TimeSpan.FromSeconds(2)));
        Assert.False(viewModel.CanShareRoomCode);
        Assert.False(viewModel.CanRetryProjection);
        Assert.True(viewModel.RecoverRoomCodeCommand.CanExecute(null));

        var recovered = conflicted with { RoomCode = "NEW133", RowVersion = "session-rv-2" };
        api.SessionDetailResponse = new(recovered, [], "{}", Capacity: 36);
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            false,
            SyncStatus.Pending,
            "PUBLICCLOUD_PROJECTION_PENDING",
            "Đang đồng bộ.",
            0));
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_READY",
            "Sẵn sàng.",
            0));
        viewModel.RecoverRoomCodeCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/sessions/{sessionId}/room-code")
                && viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<ChangePublicCloudRoomCodeRequest>(Assert.Single(api.PutRequests));
        Assert.Null(request.NewRoomCode);
        Assert.Equal("session-rv-1", request.RowVersion);
        Assert.Equal("NEW133", viewModel.RoomCode);
        Assert.False(viewModel.CanRecoverRoomCode);
    }

    [Fact]
    public async Task ExistingPublicCloudConflict_RestoresShareLockAndUsesSelectedRowVersion()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Kiểm tra đã lưu",
            "Tin",
            30,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Published,
            1,
            1,
            "exam-rv");
        var sessionId = Guid.NewGuid();
        var existing = new SessionSummaryDto(
            sessionId,
            exam.Id,
            exam.Title,
            "EXIST133",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "persisted-row-version",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [existing]
        };
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            false,
            SyncStatus.Conflict,
            ErrorCodes.RoomCodeConflict,
            "Mã phòng PublicCloud đang được sử dụng.",
            1));
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 2);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.CanShareRoomCode);
        Assert.True(viewModel.CanRecoverRoomCode);
        Assert.False(viewModel.CanRetryProjection);
        var recovered = existing with { RoomCode = "RESTORED133", RowVersion = "next-row-version" };
        api.SessionDetailResponse = new(recovered, [], "{}", Capacity: 36);
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_READY",
            "Sẵn sàng.",
            0));

        viewModel.RecoverRoomCodeCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/sessions/{sessionId}/room-code")
                && viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<ChangePublicCloudRoomCodeRequest>(Assert.Single(api.PutRequests));
        Assert.Null(request.NewRoomCode);
        Assert.Equal("persisted-row-version", request.RowVersion);
    }

    [Fact]
    public async Task ExamQuickCreate_DefaultsToNoClassAssignment()
    {
        var rule = new FileRuleDto([".pdf"], 1024, 2048, 1, false, true);
        var created = new ExamDetailDto(
            Guid.NewGuid(),
            null,
            "Kiểm tra nhanh",
            "Tin",
            null,
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Draft,
            1,
            rule,
            [],
            "exam-rv");
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [],
            ExamDetailResponse = created
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.Title = created.Title;
        viewModel.Subject = created.Subject;
        viewModel.Duration = created.DurationMinutes.ToString();

        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/exams"),
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<CreateExamRequest>(api.PostRequests[0]);
        Assert.Null(request.ClassId);
    }

    [Fact]
    public void ProductionXaml_HasNoAdvancedClassCreationOrTechnicalSettings()
    {
        var views = FindViewsDirectory();
        var exam = File.ReadAllText(Path.Combine(views, "ExamManagementView.xaml"));
        var session = File.ReadAllText(Path.Combine(views, "SessionManagementView.xaml"));
        var settings = File.ReadAllText(Path.Combine(views, "SettingsPageView.xaml"));

        Assert.DoesNotContain("Nâng cao — Gắn với lớp học", exam, StringComparison.Ordinal);
        Assert.DoesNotContain("Nâng cao — Luồng lớp học", session, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "Publishable", "Project URL", "Organization", "Secret key",
                     "CloudAccessMode", "TrustedServer", "UserSession",
                     "Discovery port", "Chunk", "Concurrent uploads", "Supabase password"
                 })
        {
            Assert.DoesNotContain(forbidden, settings, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SelectableRows_KeepCheckboxStateSeparateAndEnforceSessionEligibility()
    {
        var finished = new SelectableSessionRow(Session(SessionStatus.Finished));
        var cancelled = new SelectableSessionRow(Session(SessionStatus.Cancelled));
        var running = new SelectableSessionRow(Session(SessionStatus.InProgress));

        finished.IsChecked = true;
        cancelled.IsChecked = true;
        running.IsChecked = true;

        Assert.True(finished.IsChecked);
        Assert.True(cancelled.IsChecked);
        Assert.False(running.IsChecked);
        Assert.False(running.CanArchive);
    }

    private static SessionSummaryDto Session(SessionStatus status) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Bài kiểm tra",
        status + "-ROOM",
        status,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        new(0, 0, 0, 0, 0, 0, 0),
        1,
        "rv");

    private static string FindViewsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "frontend",
                "src",
                "ExamTransfer.Desktop",
                "Views");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Không tìm thấy thư mục Views của frontend.");
    }

    private sealed class StubLanDiscovery(
        IReadOnlyList<DiscoveryServerDto> servers,
        IReadOnlyList<OpenSessionDiscoveryDto> rooms) : ILanDiscoveryService
    {
        public Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
            TimeSpan timeout,
            string? roomCode = null,
            CancellationToken ct = default) =>
            Task.FromResult(new LanDiscoverySnapshot(servers, rooms, "test-request", servers.Count));

        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult(servers);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult(rooms);

        public Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
            string roomCode,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<OpenSessionDiscoveryDto?>(
                rooms.SingleOrDefault(x => x.RoomCode.Equals(roomCode, StringComparison.OrdinalIgnoreCase)));
    }
}
