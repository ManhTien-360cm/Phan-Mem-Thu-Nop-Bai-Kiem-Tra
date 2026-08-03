using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class LanRoomJoinAndLifecycleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ModeRouter_LanOnlyWithCloudAvailable_CallsOnlyLan()
    {
        var lan = 0;
        var cloud = 0;

        await StudentConnectJoinRouter.DispatchAsync(
            SessionAccessMode.LanOnly,
            _ => { lan++; return Task.CompletedTask; },
            _ => { cloud++; return Task.CompletedTask; },
            default);

        Assert.Equal(1, lan);
        Assert.Equal(0, cloud);
    }

    [Fact]
    public async Task ModeRouter_PublicCloudWithLanRoomSelected_CallsOnlyPublicCloud()
    {
        var lan = 0;
        var cloud = 0;

        await StudentConnectJoinRouter.DispatchAsync(
            SessionAccessMode.PublicCloud,
            _ => { lan++; return Task.CompletedTask; },
            _ => { cloud++; return Task.CompletedTask; },
            default);

        Assert.Equal(0, lan);
        Assert.Equal(1, cloud);
    }

    [Fact]
    public void PublicCloudValidRoomCode_EnablesImmediatelyAndUsesCloudJoinOnly()
    {
        var auth = StudentAuth();
        var state = new StudentSessionState();
        var cloudCalls = 0;
        var sessionId = Guid.NewGuid();
        using var viewModel = new StudentConnectViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            auth,
            new RecordingDiscovery(Room(Guid.NewGuid(), Guid.NewGuid())),
            _ => throw new InvalidOperationException("LAN path must not be called."),
            () => true,
            (roomCode, _) =>
            {
                cloudCalls++;
                Assert.Equal("ROOM42", roomCode);
                return Task.FromResult(new PublicCloudJoinResult(
                    sessionId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ParticipantStatus.PendingApproval,
                    SessionStatus.Waiting,
                    roomCode,
                    "Cloud exam",
                    "Tin",
                    45,
                    ExamDeliveryType.FileSubmission,
                    SupervisionMode.None,
                    QuizResultPolicy.Hidden,
                    null,
                    40,
                    1,
                    "cloud-access-token"));
            },
            (_, _) => Task.CompletedTask);
        viewModel.SelectedRoom = new OpenRoomCard(Room(Guid.NewGuid(), Guid.NewGuid()));
        viewModel.SelectedAccessMode = SessionAccessMode.PublicCloud;
        viewModel.RoomCode = "room42";

        Assert.True(viewModel.JoinCommand.CanExecute(null));
        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && state.SessionId == sessionId,
            TimeSpan.FromSeconds(3)));

        Assert.Equal(1, cloudCalls);
        Assert.Equal(SessionAccessMode.PublicCloud, state.AccessMode);
        Assert.Equal(ParticipantStatus.PendingApproval, state.ParticipantStatus);
        auth.Clear();
    }

    [Fact]
    public void BackendClient_RejectsDiscoveredLoopbackAndDoesNotReuseTokenAcrossOrigins()
    {
        var client = new BackendClient("http://10.0.0.5:5048");
        client.SetAccountToken("server-a-token");
        Assert.True(client.HasTrustedAccountToken);

        Assert.True(client.TrySetBaseAddress("192.168.1.7", 5048, out var error), error);
        Assert.False(client.HasTrustedAccountToken);
        Assert.False(client.TrySetBaseAddress("127.0.0.1", 5048, out error));
        Assert.Contains("localhost", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualLanRoomCode_ResolvesExactEndpointAndPostsPendingJoin()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var examId = Guid.NewGuid();
        var room = Room(sessionId, examId);
        var discovery = new RecordingDiscovery(room);
        var handler = new JoinRecordingHandler(room, participantId);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("server-account-token");
        var auth = StudentAuth();
        var state = new StudentSessionState();
        using var viewModel = new StudentConnectViewModel(
            api,
            state,
            auth,
            discovery,
            _ => Task.CompletedTask);
        viewModel.SelectedAccessMode = SessionAccessMode.LanOnly;
        viewModel.RoomCode = " room42 ";

        Assert.True(viewModel.JoinCommand.CanExecute(null));
        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && state.ParticipantId == participantId,
            TimeSpan.FromSeconds(3)));

        Assert.Equal(1, discovery.ResolveCalls);
        Assert.Equal("192.168.1.7", api.BaseAddress.Host);
        Assert.Equal(5048, api.BaseAddress.Port);
        Assert.Equal(
            ["http://192.168.1.7:5048/api/v1/discovery/identity", "http://192.168.1.7:5048/api/v1/sessions/join"],
            handler.RequestUris);
        Assert.All(handler.RequestUris, x => Assert.DoesNotContain("localhost", x, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("server-account-token", handler.JoinAuthorization);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(examId, state.ExamId);
        Assert.Equal(ParticipantStatus.PendingApproval, state.ParticipantStatus);
        Assert.Equal("Đã gửi yêu cầu, chờ giáo viên duyệt.", viewModel.Status);
        auth.Clear();
    }

    [Fact]
    public void ServerChange_ReauthenticatesAfterIdentityAndNeverSendsServerAToken()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = Room(sessionId, Guid.NewGuid());
        var auth = StudentAuth();
        auth.SetTransientCredentials("student01", "temporary-password");
        var handler = new JoinRecordingHandler(room, participantId, auth.CurrentAccount);
        var api = new BackendClient("http://10.0.0.5:5048", handler);
        api.SetAccountToken("server-a-token");
        var state = new StudentSessionState();
        using var viewModel = new StudentConnectViewModel(
            api,
            state,
            auth,
            new RecordingDiscovery(room),
            _ => Task.CompletedTask);
        viewModel.RoomCode = room.RoomCode;

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && state.ParticipantId == participantId,
            TimeSpan.FromSeconds(3)));

        Assert.Equal("server-b-token", handler.JoinAuthorization);
        Assert.DoesNotContain(handler.AuthorizationHeaders, x => x == "server-a-token");
        Assert.Contains(
            "http://192.168.1.7:5048/api/v1/auth/login",
            handler.RequestUris);
        Assert.True(api.HasTrustedAccountToken);
        auth.Clear();
    }

    [Fact]
    public void LanTimeout_ShowsTypedMessageAndReenablesButtonWithoutLosingCode()
    {
        var auth = StudentAuth();
        using var viewModel = new StudentConnectViewModel(
            new BackendClient("http://localhost:5048"),
            new StudentSessionState(),
            auth,
            new RecordingDiscovery(null),
            _ => Task.CompletedTask);
        viewModel.RoomCode = "ROOM42";

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && viewModel.Status.Contains("DISCOVERY_TIMEOUT", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3)));

        Assert.Equal("ROOM42", viewModel.RoomCode);
        Assert.True(viewModel.JoinCommand.CanExecute(null));
        auth.Clear();
    }

    [Fact]
    public void LanPostJoinSynchronizationFailure_RetainsIdentityAndReturnsTypedSyncFailure()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = Room(sessionId, Guid.NewGuid());
        var handler = new JoinRecordingHandler(room, participantId);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("server-account-token");
        var auth = StudentAuth();
        var state = new StudentSessionState();
        var completionCalls = 0;
        using var viewModel = new StudentConnectViewModel(
            api,
            state,
            auth,
            new RecordingDiscovery(room),
            _ =>
            {
                completionCalls++;
                throw new IOException("simulated post-join realtime failure");
            });
        viewModel.RoomCode = room.RoomCode;

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && completionCalls == 1,
            TimeSpan.FromSeconds(3)));

        Assert.True(state.HasSession);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(participantId, state.ParticipantId);
        Assert.Equal("participant-token", state.AccessToken);
        Assert.Contains(
            StudentJoinErrorCodes.PostJoinSynchronizationFailed,
            viewModel.Status,
            StringComparison.Ordinal);
        Assert.Equal("warning", viewModel.StatusTone);
        Assert.Equal(
            StudentJoinErrorCodes.PostJoinSynchronizationFailed,
            viewModel.LastJoinOutcome?.Code);
        Assert.True(viewModel.LastJoinOutcome?.AuthorityMutationCommitted);
        Assert.True(state.JoinMutationCommitted);
        Assert.True(state.PostJoinSynchronizationPending);
        Assert.Equal(1, handler.JoinCalls);
        auth.Clear();
    }

    [Fact]
    public void LanRetryAfterPostJoinFailure_RunsOnlySynchronization()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = Room(sessionId, Guid.NewGuid());
        var handler = new JoinRecordingHandler(room, participantId);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("server-account-token");
        var auth = StudentAuth();
        var state = new StudentSessionState();
        var completionCalls = 0;
        using var viewModel = new StudentConnectViewModel(
            api,
            state,
            auth,
            new RecordingDiscovery(room),
            _ =>
            {
                completionCalls++;
                throw new IOException("simulated post-join timeline failure");
            });
        viewModel.RoomCode = room.RoomCode;

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && handler.JoinCalls == 1,
            TimeSpan.FromSeconds(3)));
        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && completionCalls == 2,
            TimeSpan.FromSeconds(3)));

        Assert.Equal(1, handler.JoinCalls);
        Assert.Single(handler.JoinNonces);
        Assert.Equal(participantId, state.ParticipantId);
        auth.Clear();
    }

    [Fact]
    public void PublicCloudPostJoinSynchronizationFailure_RetainsIdentityAndRetriesOnlySynchronization()
    {
        var auth = StudentAuth();
        var state = new StudentSessionState();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var examId = Guid.NewGuid();
        var joinCalls = 0;
        var completionCalls = 0;
        PublicCloudJoinResult JoinResult() => new(
            SessionId: sessionId,
            ExamId: examId,
            ParticipantId: participantId,
            ParticipantStatus: ParticipantStatus.PendingApproval,
            SessionStatus: SessionStatus.Waiting,
            RoomCode: "ROOM42",
            ExamTitle: "Cloud exam",
            Subject: "Tin",
            DurationMinutes: 45,
            DeliveryType: ExamDeliveryType.FileSubmission,
            SupervisionMode: SupervisionMode.None,
            QuizResultPolicy: QuizResultPolicy.Hidden,
            PlannedStartUtc: null,
            Capacity: 40,
            CurrentParticipantCount: 1,
            AccessToken: "cloud-access-token");
        using var viewModel = new StudentConnectViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            auth,
            new RecordingDiscovery(null),
            _ => throw new InvalidOperationException("LAN path must not run."),
            () => true,
            (_, _) =>
            {
                joinCalls++;
                return Task.FromResult(JoinResult());
            },
            (_, _) =>
            {
                completionCalls++;
                throw new IOException("simulated post-join timeline failure");
            });
        viewModel.SelectedAccessMode = SessionAccessMode.PublicCloud;
        viewModel.RoomCode = "ROOM42";

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && joinCalls == 1,
            TimeSpan.FromSeconds(3)));
        Assert.True(state.HasSession);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(examId, state.ExamId);
        Assert.Equal(participantId, state.ParticipantId);
        Assert.Equal("cloud-access-token", state.AccessToken);
        Assert.Contains(
            StudentJoinErrorCodes.PostJoinSynchronizationFailed,
            viewModel.Status,
            StringComparison.Ordinal);
        Assert.Equal(
            StudentJoinErrorCodes.PostJoinSynchronizationFailed,
            viewModel.LastJoinOutcome?.Code);

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && completionCalls == 2,
            TimeSpan.FromSeconds(3)));

        Assert.Equal(2, completionCalls);
        Assert.Equal(1, joinCalls);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(examId, state.ExamId);
        Assert.Equal(participantId, state.ParticipantId);
        auth.Clear();
    }

    [Fact]
    public void PublicCloudTrueRoomNotFound_RetryReissuesAuthoritativeMutation()
    {
        var auth = StudentAuth();
        var state = new StudentSessionState();
        var joinCalls = 0;
        var completionCalls = 0;
        using var viewModel = new StudentConnectViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            auth,
            new RecordingDiscovery(null),
            _ => throw new InvalidOperationException("LAN path must not run."),
            () => true,
            (_, _) =>
            {
                joinCalls++;
                throw new PublicCloudApiException(
                    "OPEN_PUBLIC_SESSION_NOT_FOUND",
                    "room not found",
                    HttpStatusCode.NotFound);
            },
            (_, _) =>
            {
                completionCalls++;
                return Task.CompletedTask;
            });
        viewModel.SelectedAccessMode = SessionAccessMode.PublicCloud;
        viewModel.RoomCode = "ROOM42";

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && joinCalls == 1,
            TimeSpan.FromSeconds(3)));
        Assert.False(state.HasSession);
        Assert.False(state.JoinMutationCommitted);
        Assert.Equal(StudentJoinErrorCodes.RoomNotFound, viewModel.LastJoinOutcome?.Code);
        Assert.Contains("OPEN_PUBLIC_SESSION_NOT_FOUND", viewModel.Status, StringComparison.Ordinal);

        viewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !viewModel.IsBusy && joinCalls == 2,
            TimeSpan.FromSeconds(3)));
        Assert.Equal(0, completionCalls);
        Assert.False(state.HasSession);
        auth.Clear();
    }

    [Fact]
    public void ProductionXaml_UpdatesRoomCodeImmediatelyAndHasNoManualIpOrPort()
    {
        var xaml = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "StudentConnectView.xaml"));

        Assert.Contains("Mode=TwoWay", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("ValidationMessage", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Địa chỉ IP", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Cổng\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_ServerAbsent_StartsExactlyOneProcessAndWaitsForIdentity()
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { BecomeHealthyAfterStart = true };
        var service = new LocalServerLifecycleService(runtime, layout.Client);

        var result = await service.EnsureStartedAsync(UserRole.Teacher);

        Assert.Equal("SERVER_STARTED", result.Status);
        Assert.True(result.Started);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(Path.GetDirectoryName(layout.ServerExe), runtime.WorkingDirectory);
        await service.StopOwnedAsync();
        Assert.True(runtime.StartedProcess?.Stopped);
        Assert.Equal(0, runtime.StopExactCount);
    }

    [Fact]
    public void LocalServerRuntime_MapsValidatedDesktopPublicCloudEnvironmentToChildConfiguration()
    {
        var organizationId = Guid.NewGuid();
        var publishableKey = string.Concat("sb_", "publishable_test_only");
        var values = new Dictionary<string, string?>
        {
            ["EXAMTRANSFER_SUPABASE_URL"] = "https://staging.example.supabase.co/",
            ["EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY"] = publishableKey,
            ["EXAMTRANSFER_ORGANIZATION_ID"] = organizationId.ToString()
        };
        var startInfo = new System.Diagnostics.ProcessStartInfo();

        LocalServerRuntime.ApplyPublicCloudEnvironment(
            startInfo,
            name => values.GetValueOrDefault(name));

        Assert.Equal("true", startInfo.Environment["Cloud__Enabled"]);
        Assert.Equal(
            "https://staging.example.supabase.co",
            startInfo.Environment["Cloud__SupabaseUrl"]);
        Assert.Equal(
            publishableKey,
            startInfo.Environment["Cloud__PublishableKey"]);
        Assert.Equal(
            publishableKey,
            startInfo.Environment["Cloud__SupabasePublishableKey"]);
        Assert.Equal(
            organizationId.ToString(),
            startInfo.Environment["Cloud__OrganizationId"]);
        Assert.Equal(
            CloudAccessModes.UserSession,
            startInfo.Environment["Cloud__AccessMode"]);
        Assert.DoesNotContain(
            startInfo.Environment.Keys,
            key => key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || key.Contains("SERVICE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalServerRuntime_DoesNotForwardSecretOrIncompletePublicCloudConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["EXAMTRANSFER_SUPABASE_URL"] = "https://staging.example.supabase.co",
            ["EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY"] = string.Concat("sb_", "secret_test_only"),
            ["EXAMTRANSFER_ORGANIZATION_ID"] = Guid.NewGuid().ToString()
        };
        var startInfo = new System.Diagnostics.ProcessStartInfo();

        LocalServerRuntime.ApplyPublicCloudEnvironment(
            startInfo,
            name => values.GetValueOrDefault(name));

        Assert.False(startInfo.Environment.ContainsKey("Cloud__Enabled"));
        Assert.False(startInfo.Environment.ContainsKey("Cloud__SupabaseUrl"));
        Assert.False(startInfo.Environment.ContainsKey("Cloud__PublishableKey"));
        Assert.False(startInfo.Environment.ContainsKey("Cloud__SupabasePublishableKey"));
        Assert.False(startInfo.Environment.ContainsKey("Cloud__OrganizationId"));
    }

    [Fact]
    public async Task Lifecycle_StudentNonOwnerExit_DoesNotStopExistingPackagedServer()
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { Healthy = true };
        var service = new LocalServerLifecycleService(runtime, layout.Client);
        var result = await service.EnsureStartedAsync(UserRole.Student);

        Assert.Equal("ROLE_NOT_AUTHORIZED", result.Status);
        Assert.Equal(0, runtime.StartCount);
        await service.StopOwnedAsync();
        Assert.Equal(0, runtime.StopExactCount);
        Assert.True(runtime.Healthy);
    }

    [Fact]
    public async Task Lifecycle_TeacherNonOwnerExit_DoesNotStopServerFromAnotherInstance()
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { Healthy = true };
        var service = new LocalServerLifecycleService(runtime, layout.Client);
        var result = await service.EnsureStartedAsync(UserRole.Teacher);

        Assert.Equal("SERVER_HEALTHY", result.Status);
        Assert.Equal(0, runtime.StartCount);
        await service.StopOwnedAsync();
        Assert.Equal(0, runtime.StopExactCount);
        Assert.True(runtime.Healthy);
    }

    [Theory]
    [InlineData(UserRole.Teacher)]
    [InlineData(UserRole.Admin)]
    public async Task Lifecycle_AuthorizedRole_CanExplicitlyStopExistingPackagedServer(
        UserRole role)
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { Healthy = true };
        var service = new LocalServerLifecycleService(runtime, layout.Client);

        await service.StopExistingPackagedServerAsync(role);

        Assert.Equal(1, runtime.StopExactCount);
        Assert.Equal(Path.GetFullPath(layout.ServerExe), runtime.StoppedExecutablePath);
        Assert.False(runtime.Healthy);
    }

    [Fact]
    public async Task Lifecycle_StudentCannotExplicitlyStopExistingPackagedServer()
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { Healthy = true };
        var service = new LocalServerLifecycleService(runtime, layout.Client);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.StopExistingPackagedServerAsync(UserRole.Student));

        Assert.Equal(0, runtime.StopExactCount);
        Assert.True(runtime.Healthy);
    }

    [Fact]
    public void ApplicationExit_UsesOwnershipSafeLocalServerStop()
    {
        var source = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "App.xaml.cs"));

        Assert.Contains(
            "LocalServerLifecycle.StopOwnedAsync()",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LocalServerLifecycle.StopExistingPackagedServerAsync",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_WrongProcessOnPort_ReturnsActionableConflict()
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { PortOccupied = true };
        var result = await new LocalServerLifecycleService(runtime, layout.Client)
            .EnsureStartedAsync(UserRole.Teacher);

        Assert.Equal("PORT_CONFLICT_TCP_5048", result.Status);
        Assert.Contains("tiến trình khác", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task Lifecycle_WrongProcessOnUdpDiscoveryPort_ReturnsActionableConflict()
    {
        using var layout = TeacherLayout();
        var runtime = new FakeRuntime { UdpPortOccupied = true };
        var result = await new LocalServerLifecycleService(runtime, layout.Client)
            .EnsureStartedAsync(UserRole.Teacher);

        Assert.Equal("PORT_CONFLICT_UDP_40550", result.Status);
        Assert.Contains("UDP port 40550", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.StartCount);
    }

    [Theory]
    [InlineData("DISCOVERY_PROTOCOL_MISMATCH")]
    [InlineData("CLIENT_SERVER_BUILD_MISMATCH")]
    public async Task Lifecycle_IncompatibleInstalledServer_ReturnsTypedMismatchAndDoesNotStart(
        string code)
    {
        using var layout = TeacherLayout();
        var identity = new LocalServerIdentityDto(
            "ExamTransfer.LocalServer",
            "server-1",
            code == DiscoveryProtocol.ProtocolMismatch
                ? "ExamTransfer/1"
                : DiscoveryProtocol.ProtocolVersion,
            DiscoveryProtocol.DefaultPort,
            code == DiscoveryProtocol.BuildMismatch
                ? "same-version-different-build"
                : ReleaseIdentity.BuildId,
            ReleaseIdentity.SemanticVersion,
            "192.168.1.7",
            5048);
        var runtime = new FakeRuntime
        {
            ProbeResult = new(
                code,
                identity,
                "Update required.")
        };

        var result = await new LocalServerLifecycleService(runtime, layout.Client)
            .EnsureStartedAsync(UserRole.Teacher);

        Assert.Equal(code, result.Status);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task Lifecycle_StudentRole_DoesNotStart()
    {
        var runtime = new FakeRuntime();
        var result = await new LocalServerLifecycleService(runtime, AppContext.BaseDirectory)
            .EnsureStartedAsync(UserRole.Student);

        Assert.Equal("ROLE_NOT_AUTHORIZED", result.Status);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task Lifecycle_TeacherExeMissing_ReturnsActionableError()
    {
        var runtime = new FakeRuntime();
        var result = await new LocalServerLifecycleService(runtime, AppContext.BaseDirectory)
            .EnsureStartedAsync(UserRole.Teacher);

        Assert.Equal("SERVER_EXE_MISSING", result.Status);
        Assert.Contains("chạy lại bộ cài", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public void Installer_IsUnifiedAndDoesNotStartServerBeforeAuthentication()
    {
        var source = File.ReadAllText(FindFile("installer", "ExamTransfer.iss"));

        Assert.Contains("ExamTransfer TCP 5048", source, StringComparison.Ordinal);
        Assert.Contains("protocol=TCP localport=5048", source, StringComparison.Ordinal);
        Assert.Contains("ExamTransfer UDP 40550", source, StringComparison.Ordinal);
        Assert.Contains("protocol=UDP localport=40550", source, StringComparison.Ordinal);
        Assert.Contains("ExamTransfer UDP 5050", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "firewall add rule name=\"\"ExamTransfer UDP 5050",
            source,
            StringComparison.Ordinal);
        Assert.Contains("profile=private,domain", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExamTransfer.LocalServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[Types]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[Components]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStudentOnlyInstall", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WizardIsComponentSelected", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAndVerify", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetIniString('Install', 'Role'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExamTransfer Local Server\"; Filename:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/IM {#MyServerExe}", source, StringComparison.Ordinal);
        Assert.Contains("RunLocalServerGuard('StopOnly'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerGuard_UsesExactExecutablePathAndManifestIdentity()
    {
        var guard = File.ReadAllText(FindFile(
            "scripts",
            "installer-localserver-guard.ps1"));
        var releaseEntry = File.ReadAllText(FindFile("build-release.ps1"));
        var release = File.ReadAllText(FindFile("scripts", "build-release.ps1"));

        Assert.Contains("ExecutablePath", guard, StringComparison.Ordinal);
        Assert.Contains("[StringComparison]::OrdinalIgnoreCase", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("/IM", guard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PORT_CONFLICT_TCP_5048", guard, StringComparison.Ordinal);
        Assert.Contains("PORT_CONFLICT_UDP_40550", guard, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", guard, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", guard, StringComparison.Ordinal);
        Assert.Contains("scripts\\build-release.ps1", releaseEntry, StringComparison.Ordinal);
        Assert.Contains("ExamTransferBuildId", release, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", release, StringComparison.Ordinal);
        Assert.Contains("discoveryUdpPort  = 40550", release, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveSourceGuard_HasNoLegacyDiscoveryPortOrFallback()
    {
        var root = FindRepositoryRoot();
        var files = new[]
        {
            Path.Combine(root, "backend", "src"),
            Path.Combine(root, "frontend", "src"),
            Path.Combine(root, "scripts")
        }.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && IsSourceGuardFile(path))
            .Concat(
            [
                Path.Combine(root, "backend", "Dockerfile"),
                Path.Combine(root, ".env.docker.example")
            ]);

        var violations = files
            .Where(path => File.Exists(path))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { path, line, index }))
            .Where(item => System.Text.RegularExpressions.Regex.IsMatch(
                item.line,
                @"\b5050\b|\b5051\b|5050\s*-\s*5055",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .Select(item => $"{item.path}:{item.index + 1}: {item.line.Trim()}")
            .ToList();

        Assert.Empty(violations);
    }

    private static bool IsSourceGuardFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".iss", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".example", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static AppAuthSessionState StudentAuth()
    {
        var auth = new AppAuthSessionState();
        var providerUserId = Guid.NewGuid();
        auth.SetAuthenticated(
            new CurrentAccountDto(
                providerUserId,
                "student01",
                null,
                "Học sinh",
                "HS001",
                UserRole.Student,
                null,
                Guid.NewGuid(),
                "device-1",
                DateTimeOffset.UtcNow.AddHours(1),
                new DateOnly(2010, 1, 1),
                ProviderUserId: providerUserId.ToString("D")),
            "server-account-token");
        return auth;
    }

    private static OpenSessionDiscoveryDto Room(Guid sessionId, Guid examId) =>
        new(
            sessionId,
            "ROOM42",
            "Phòng thi",
            null,
            null,
            null,
            "Kiểm tra",
            "Cô Lan",
            SessionStatus.Waiting,
            true,
            40,
            0,
            null,
            null,
            SessionAccessMode.LanOnly,
            "server-1",
            "Teacher",
            "http://192.168.1.7:5048",
            DateTimeOffset.UtcNow,
            DiscoveryProtocol.ProtocolVersion,
            "Tin",
            45,
            ExamDeliveryType.FileSubmission,
            SupervisionMode.None,
            SessionAdmissionMode.OpenRequest,
            examId);

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = segments.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ExamTransfer.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("ExamTransfer repository root was not found.");
    }

    private static TestLayout TeacherLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExamTransfer-Lifecycle-" + Guid.NewGuid().ToString("N"));
        var client = Path.Combine(root, "Client");
        var server = Path.Combine(root, "Server");
        Directory.CreateDirectory(client);
        Directory.CreateDirectory(server);
        var exe = Path.Combine(server, "ExamTransfer.LocalServer.exe");
        File.WriteAllBytes(exe, [0]);
        return new(root, client, exe);
    }

    private sealed class RecordingDiscovery(OpenSessionDiscoveryDto? room) : ILanDiscoveryService
    {
        public int ResolveCalls { get; private set; }

        public Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
            TimeSpan timeout,
            string? roomCode = null,
            CancellationToken ct = default) =>
            Task.FromResult(new LanDiscoverySnapshot(
                [],
                room is null ? [] : [room],
                "test-request",
                room is null ? 0 : 1));

        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoveryServerDto>>([]);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenSessionDiscoveryDto>>(room is null ? [] : [room]);

        public Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
            string roomCode,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(room);
        }
    }

    private sealed class JoinRecordingHandler(
        OpenSessionDiscoveryDto room,
        Guid participantId,
        CurrentAccountDto? currentAccount = null) : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];
        public List<string> JoinNonces { get; } = [];
        public int JoinCalls { get; private set; }
        public string? JoinAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
            object data;
            if (request.RequestUri.AbsolutePath.EndsWith("/discovery/identity", StringComparison.Ordinal))
            {
                data = new LocalServerIdentityDto(
                    "ExamTransfer.LocalServer",
                    room.ServerId,
                    DiscoveryProtocol.ProtocolVersion,
                    DiscoveryProtocol.DefaultPort,
                    ReleaseIdentity.BuildId,
                    ReleaseIdentity.SemanticVersion,
                    "192.168.1.7",
                    5048);
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                data = new AccountLoginResultDto(
                    true,
                    false,
                    null,
                    currentAccount!.UserId,
                    currentAccount.DisplayName,
                    currentAccount.StudentCode,
                    UserRole.Student,
                    currentAccount.OrganizationId,
                    "server-b-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    currentAccount.DeviceId);
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/auth/me", StringComparison.Ordinal))
            {
                data = currentAccount!;
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/sessions/join", StringComparison.Ordinal))
            {
                JoinCalls++;
                JoinAuthorization = request.Headers.Authorization?.Parameter;
                var requestJson = request.Content!.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                var joinRequest = JsonSerializer.Deserialize<JoinSessionRequest>(requestJson, Json)
                    ?? throw new JsonException("Join request body was empty.");
                if (string.IsNullOrWhiteSpace(joinRequest.Nonce))
                    throw new JsonException("Join request nonce was missing.");
                JoinNonces.Add(joinRequest.Nonce);
                var participant = new ParticipantDto(
                    participantId,
                    room.SessionId,
                    "HS001",
                    "Học sinh",
                    "device-1",
                    "Student",
                    "192.168.1.20",
                    "1.3.1",
                    ParticipantStatus.PendingApproval,
                    DateTimeOffset.UtcNow,
                    DownloadStatus.NotStarted,
                    SubmissionStatus.NotStarted,
                    0,
                    null,
                    ConnectionState.Online);
                data = new JoinSessionResponse(
                    room.SessionId,
                    participantId,
                    ParticipantStatus.PendingApproval,
                    "participant-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    participant);
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    ApiResponse<object>.Ok(data, "test-trace"),
                    options: Json)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeRuntime : ILocalServerRuntime
    {
        public bool Healthy { get; set; }
        public bool PortOccupied { get; set; }
        public bool UdpPortOccupied { get; set; }
        public bool BecomeHealthyAfterStart { get; set; }
        public LocalServerProbeResult? ProbeResult { get; set; }
        public int StartCount { get; private set; }
        public int StopExactCount { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public string? StoppedExecutablePath { get; private set; }
        public FakeProcess? StartedProcess { get; private set; }

        public Task<LocalServerProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(
                ProbeResult
                ?? (Healthy
                    ? new LocalServerProbeResult(
                        "READY",
                        new LocalServerIdentityDto(
                            "ExamTransfer.LocalServer",
                            "server-1",
                            DiscoveryProtocol.ProtocolVersion,
                            DiscoveryProtocol.DefaultPort,
                            ReleaseIdentity.BuildId,
                            ReleaseIdentity.SemanticVersion,
                            "192.168.1.7",
                            5048))
                    : new LocalServerProbeResult("NOT_RUNNING")));

        public Task<bool> IsTcpPortOccupiedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(PortOccupied);

        public Task<bool> IsUdpPortOccupiedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(UdpPortOccupied);

        public ILocalServerProcess Start(string executablePath, string workingDirectory)
        {
            StartCount++;
            WorkingDirectory = workingDirectory;
            if (BecomeHealthyAfterStart) Healthy = true;
            StartedProcess = new FakeProcess();
            return StartedProcess;
        }

        public Task StopExactAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopExactCount++;
            StoppedExecutablePath = Path.GetFullPath(executablePath);
            Healthy = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcess : ILocalServerProcess
    {
        public bool Stopped { get; private set; }
        public bool HasExited => Stopped;
        public int? ExitCode => Stopped ? 0 : null;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed record TestLayout(string Root, string Client, string ServerExe) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
