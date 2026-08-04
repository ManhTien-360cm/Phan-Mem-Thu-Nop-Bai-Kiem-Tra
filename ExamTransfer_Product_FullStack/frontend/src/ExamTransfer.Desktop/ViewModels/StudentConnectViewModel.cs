using System.Collections.ObjectModel;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentConnectViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly AppAuthSessionState authState;
    private readonly ILanDiscoveryService discovery;
    private readonly Func<CancellationToken, Task> completeLanJoin;
    private readonly Func<bool> publicCloudReady;
    private readonly Func<string?> publicCloudConfigurationError;
    private readonly Func<string, CancellationToken, Task<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult>> joinPublicCloud;
    private readonly Func<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult, CancellationToken, Task> completePublicCloudJoin;
    private string ip = "127.0.0.1";
    private string port = "5048";
    private string roomCode = string.Empty;
    private string displayName;
    private string studentCode;
    private string className = string.Empty;
    private string classCode = string.Empty;
    private bool isScanning;
    private SessionAccessMode selectedAccessMode = SessionAccessMode.LanOnly;
    private string validationMessage = "Nhập mã phòng để tham gia trong mạng LAN.";
    private OpenRoomCard? selectedRoom;
    private ServerCard? selectedServer;
    private StudentJoinOutcome? lastJoinOutcome;
    private ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult? committedPublicCloudJoin;

    public StudentConnectViewModel(
        IBackendClient api,
        StudentSessionState state,
        AppAuthSessionState authState,
        ILanDiscoveryService? discovery = null,
        Func<CancellationToken, Task>? completeLanJoin = null,
        Func<bool>? publicCloudReady = null,
        Func<string, CancellationToken, Task<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult>>? joinPublicCloud = null,
        Func<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult, CancellationToken, Task>? completePublicCloudJoin = null,
        Func<string?>? publicCloudConfigurationError = null)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        this.discovery = discovery ?? AppServices.LanDiscovery;
        this.completeLanJoin = completeLanJoin ?? CompleteLanJoinAsync;
        this.publicCloudReady = publicCloudReady
            ?? (() => AppServices.PublicCloud.Configured && AppServices.PublicCloud.Authenticated);
        this.publicCloudConfigurationError = publicCloudConfigurationError
            ?? (() => AppServices.PublicCloud.ConfigurationErrorCode
                ?? (AppServices.PublicCloud.Configured
                    ? "PUBLICCLOUD_AUTH_EXPIRED"
                    : "PUBLICCLOUD_NOT_CONFIGURED"));
        this.joinPublicCloud = joinPublicCloud ?? ((code, ct) =>
            AppServices.PublicCloud.JoinByRoomCodeAsync(
                code,
                Environment.MachineName + "-" + Environment.UserName,
                Environment.MachineName,
                "1.0.0",
                ct,
                () =>
                {
                    Status = "Phòng đang đồng bộ PublicCloud; ứng dụng sẽ thử lại trong thời gian giới hạn.";
                    StatusTone = "warning";
                }));
        this.completePublicCloudJoin = completePublicCloudJoin ?? CompletePublicCloudJoinAsync;
        ip = api.BaseAddress.Host;
        port = api.BaseAddress.Port.ToString();
        displayName = authState.CurrentAccount?.DisplayName ?? string.Empty;
        studentCode = authState.CurrentAccount?.StudentCode ?? string.Empty;
        ScanCommand = new AsyncRelayCommand(() => ScanAsync(DisposeToken), () => !IsScanning && !IsBusy);
        JoinCommand = new AsyncRelayCommand(JoinAsync, CanJoin);
    }

    public ObservableCollection<OpenRoomCard> Rooms { get; } = new();
    public ObservableCollection<ServerCard> Servers { get; } = new();
    public IReadOnlyList<ReadinessItem> Readiness { get; } =
    [
        new("Kết nối mạng", "LAN/Wi-Fi đang hoạt động", true),
        new("Dung lượng trống", "Đủ dung lượng cho đề và bài làm", true),
        new("Quyền ghi", "Thư mục ExamTransfer có thể sử dụng", true),
        new("Định danh thiết bị", Environment.MachineName, true)
    ];
    public bool HasRooms => SelectedAccessMode == SessionAccessMode.LanOnly && Rooms.Count > 0;
    public bool HasNoRooms => SelectedAccessMode == SessionAccessMode.LanOnly && Rooms.Count == 0;

    public string Ip { get => ip; set { if (Set(ref ip, value)) RaiseCommands(); } }
    public string Port { get => port; set { if (Set(ref port, value)) RaiseCommands(); } }
    public string RoomCode
    {
        get => roomCode;
        set
        {
            var normalized = RoomCodeRules.Normalize(value);
            if (!Set(ref roomCode, normalized)) return;
            UpdateValidation();
            RaiseCommands();
        }
    }
    public string DisplayName { get => displayName; set { if (Set(ref displayName, value)) RaiseCommands(); } }
    public string StudentCode { get => studentCode; set { if (Set(ref studentCode, value)) RaiseCommands(); } }
    public string ClassName { get => className; set => Set(ref className, value); }
    public string ClassCode { get => classCode; set { if (Set(ref classCode, value)) RaiseCommands(); } }
    public bool IsScanning { get => isScanning; private set { if (Set(ref isScanning, value)) RaiseCommands(); } }
    public SessionAccessMode SelectedAccessMode
    {
        get => selectedAccessMode;
        set
        {
            if (!Set(ref selectedAccessMode, value)) return;
            Raise(nameof(IsLanMode));
            Raise(nameof(IsPublicCloudMode));
            Raise(nameof(HasRooms));
            Raise(nameof(HasNoRooms));
            UpdateValidation();
            RaiseCommands();
        }
    }
    public bool IsLanMode
    {
        get => SelectedAccessMode == SessionAccessMode.LanOnly;
        set
        {
            if (value) SelectedAccessMode = SessionAccessMode.LanOnly;
        }
    }
    public bool IsPublicCloudMode
    {
        get => SelectedAccessMode == SessionAccessMode.PublicCloud;
        set
        {
            if (value) SelectedAccessMode = SessionAccessMode.PublicCloud;
        }
    }
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }
    public ServerCard? SelectedServer
    {
        get => selectedServer;
        set
        {
            if (!Set(ref selectedServer, value) || value is null) return;
            Ip = value.Ip;
            Port = value.Port.ToString();
            Status = $"Đã chọn {value.Name}";
            StatusTone = "primary";
        }
    }

    public OpenRoomCard? SelectedRoom
    {
        get => selectedRoom;
        set
        {
            if (!Set(ref selectedRoom, value) || value is null) return;
            RoomCode = value.RoomCode;
            if (Uri.TryCreate(value.BaseAddress, UriKind.Absolute, out var endpoint))
            {
                Ip = endpoint.Host;
                Port = endpoint.Port.ToString();
            }
            Status = $"Đã chọn kỳ thi {value.ExamTitle}";
            StatusTone = "primary";
        }
    }

    public ICommand ScanCommand { get; }
    public ICommand JoinCommand { get; }
    public StudentJoinOutcome? LastJoinOutcome
    {
        get => lastJoinOutcome;
        private set => Set(ref lastJoinOutcome, value);
    }

    protected override Task LoadAsync(CancellationToken ct) => ScanAsync(ct);

    private async Task ScanAsync(CancellationToken ct)
    {
        if (IsScanning) return;
        try
        {
            IsScanning = true;
            Status = "Đang tìm máy chủ và phòng thi trong mạng LAN";
            StatusTone = "primary";
            Servers.Clear();
            Rooms.Clear();
            var snapshot = await discovery.DiscoverSnapshotAsync(TimeSpan.FromSeconds(2), null, ct);
            foreach (var server in snapshot.Servers)
            {
                Servers.Add(new(
                    server.ServerName,
                    "Máy giáo viên",
                    server.Address,
                    server.Port,
                    0,
                    "Sẵn sàng",
                    "success",
                    server.ActiveRoomCount,
                    0,
                    server.Fingerprint,
                    server.Version));
            }
            foreach (var room in snapshot.Rooms)
                Rooms.Add(new(room));
            Raise(nameof(HasRooms));
            Raise(nameof(HasNoRooms));

            SelectedRoom = Rooms.FirstOrDefault();
            SelectedServer = Servers.FirstOrDefault();
            if (Rooms.Count == 0)
            {
                Status = "Chưa tìm thấy kỳ thi LanOnly đang chờ. Có thể quét lại hoặc nhập mã phòng.";
                StatusTone = "warning";
            }
            else
            {
                Status = $"Đã tìm thấy {Servers.Count} máy chủ và {Rooms.Count} phòng đang mở";
                StatusTone = "success";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "StudentConnect.Scan");
            Status = "Không tìm thấy máy giáo viên trong mạng hiện tại. Hãy kiểm tra Wi-Fi/LAN rồi quét lại.";
            StatusTone = "warning";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanJoin() => !IsBusy
        && authState.IsStudent
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(StudentCode)
        && (RoomCodeRules.IsValid(RoomCode) || (IsLanMode && SelectedRoom is not null))
        && (SelectedAccessMode == SessionAccessMode.PublicCloud
            ? publicCloudReady()
            : true);

    private async Task JoinAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            FrontendLogger.LogMessage(
                $"selected_mode={SelectedAccessMode}; room={MaskRoomCode(RoomCode)}; phase=dispatch",
                "StudentConnect.Join");
            var requestedCode = RoomCodeRules.Normalize(RoomCode);
            if (state.CanResumePostJoinSynchronization(
                    SelectedAccessMode,
                    requestedCode))
            {
                FrontendLogger.LogMessage(
                    $"mode={SelectedAccessMode}; phase=post_join_retry; session_id={state.SessionId}; "
                    + $"participant_id={state.ParticipantId}; authority_mutation_committed=yes",
                    "StudentLifecycle.PostJoin");
                LastJoinOutcome = await CompleteCommittedJoinAsync(
                    SelectedAccessMode,
                    DisposeToken);
            }
            else
            {
                await StudentConnectJoinRouter.DispatchAsync(
                    SelectedAccessMode,
                    JoinLanAsync,
                    JoinPublicCloudAsync,
                    DisposeToken);
            }
            if (!IsDisposed && LastJoinOutcome?.Succeeded == true)
            {
                Status = "Đã gửi yêu cầu, chờ giáo viên duyệt.";
                StatusTone = "success";
            }
        }
        catch (LanJoinException ex)
        {
            ReportJoinFailure(ex.Code, ex.Message, ex, ClassifyJoinFailure(ex.Code));
        }
        catch (BackendApiException ex)
        {
            var displayCode = MapBackendCode(ex.ApiCode);
            ReportJoinFailure(displayCode, ex.Message, ex, ClassifyJoinFailure(displayCode));
        }
        catch (Exception ex) when (SelectedAccessMode == SessionAccessMode.PublicCloud)
        {
            var error = MapPublicJoinError(ex);
            ReportJoinFailure(
                error.Code,
                error.Message,
                ex,
                error.TypedCode);
        }
        catch (HttpRequestException ex)
        {
            ReportJoinFailure(
                "NETWORK_ERROR",
                "Không thể kết nối tới máy giáo viên.",
                ex,
                StudentJoinErrorCodes.JoinMutationFailed);
        }
        catch (Exception ex)
        {
            ReportJoinFailure(
                "JOIN_REJECTED",
                "Không thể hoàn tất yêu cầu tham gia.",
                ex,
                StudentJoinErrorCodes.JoinMutationFailed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task JoinLanAsync(CancellationToken ct)
    {
        var requestedCode = RoomCodeRules.Normalize(RoomCode);
        if (!RoomCodeRules.IsValid(requestedCode))
            throw new LanJoinException("ROOM_CODE_INVALID", RoomCodeRules.ValidationMessage);

        Status = "Đang tìm máy giáo viên...";
        StatusTone = "primary";
        var room = SelectedRoom?.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase) == true
            ? SelectedRoom
            : Rooms.FirstOrDefault(x => x.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase));
        if (room is null)
        {
            try
            {
                var discovered = await discovery.DiscoverByRoomCodeAsync(
                    requestedCode,
                    TimeSpan.FromSeconds(4),
                    ct);
                if (discovered is null)
                    throw new LanJoinException(
                        "DISCOVERY_TIMEOUT",
                        "Không tìm thấy mã phòng này trong mạng LAN.");
                room = new OpenRoomCard(discovered);
            }
            catch (LanDiscoveryException ex)
            {
                throw new LanJoinException(ex.Code, ex.Message, ex);
            }
        }

        Status = "Đã tìm thấy máy chủ...";
        var endpoint = new Uri(room.BaseAddress);
        if (!api.TrySetBaseAddress(
                endpoint.GetLeftPart(UriPartial.Authority),
                endpoint.Port,
                out var endpointError))
            throw new LanJoinException(
                "ENDPOINT_UNREACHABLE",
                endpointError ?? "Đã tìm thấy phòng nhưng không thể kết nối tới máy giáo viên.");

        Status = "Đang xác minh phòng...";
        LocalServerIdentityDto identity;
        try
        {
            identity = ApiGuard.Require(await api.GetAsync<LocalServerIdentityDto>(
                "api/v1/discovery/identity",
                ct));
        }
        catch (BackendApiException ex) when (ex.HttpStatusCode == 404)
        {
            throw new LanJoinException(
                DiscoveryProtocol.ProtocolMismatch,
                "Local Server không có identity endpoint V2. Hãy cập nhật đồng bộ máy giáo viên và học sinh.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LanJoinException(
                "ENDPOINT_UNREACHABLE",
                "Đã tìm thấy phòng nhưng không thể kết nối tới máy giáo viên.",
                ex);
        }
        if (!identity.Protocol.Equals(DiscoveryProtocol.ProtocolVersion, StringComparison.Ordinal))
            throw new LanJoinException(
                DiscoveryProtocol.ProtocolMismatch,
                "Local Server dùng discovery protocol không tương thích.");
        if (identity.DiscoveryPort != DiscoveryProtocol.DefaultPort)
            throw new LanJoinException(
                DiscoveryProtocol.PortMismatch,
                $"Local Server không dùng UDP {DiscoveryProtocol.DefaultPort}.");
        if (!identity.BuildId.Equals(ReleaseIdentity.BuildId, StringComparison.Ordinal))
            throw new LanJoinException(
                DiscoveryProtocol.BuildMismatch,
                "Client và Local Server không cùng BuildId. Hãy cài lại cùng bộ cài ExamTransfer.");
        if (!identity.Product.Equals("ExamTransfer.LocalServer", StringComparison.Ordinal)
            || !identity.ServerId.Equals(room.Room.ServerId, StringComparison.OrdinalIgnoreCase)
            || !identity.AdvertisedAddress.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
            || identity.ServerPort != endpoint.Port)
            throw new LanJoinException(
                "TOKEN_SERVER_MISMATCH",
                "Máy chủ phản hồi không khớp danh tính của phòng đã tìm thấy.");

        api.SetParticipantToken(null); // Phải xóa participant token cũ TRƯỚC khi authenticate, tránh gửi token phòng cũ cùng với request join phòng mới
        await EnsureLocalAccountAsync(identity.ServerId, ct);
        state.Reset();
        Status = "Đang gửi yêu cầu tham gia...";
        var request = new JoinSessionRequest(
            requestedCode,
            StudentCode.Trim(),
            DisplayName.Trim(),
            null,
            Environment.MachineName + "-" + Environment.UserName,
            Environment.MachineName,
            "1.0.0",
            Guid.NewGuid().ToString("N"));

        ApiResponse<JoinSessionResponse>? joinResponse = null;
        try
        {
            joinResponse = await api.PostAsync<JoinSessionRequest, JoinSessionResponse>(
                "api/v1/sessions/join",
                request,
                ct);
        }
        catch (BackendApiException ex) when (ex.HttpStatusCode == 401)
        {
            // Token local expired on server, clear stale token and re-authenticate via transient credentials
            api.SetAccountToken(null);
            await EnsureLocalAccountAsync(identity.ServerId, ct);
            joinResponse = await api.PostAsync<JoinSessionRequest, JoinSessionResponse>(
                "api/v1/sessions/join",
                request,
                ct);
        }

        var response = ApiGuard.Require(joinResponse);
        state.ApplyJoin(response, request.RoomCode, request.StudentCode, request.DisplayName, SessionAccessMode.LanOnly, room.Room.ServerId);
        state.ExamId = room.Room.ExamId;
        state.AdmissionMode = room.Room.AdmissionMode;
        state.ExamTitle = room.Room.ExamTitle;
        state.Subject = room.Room.Subject;
        state.DurationMinutes = room.Room.DurationMinutes;
        state.DeliveryType = room.Room.DeliveryType;
        state.SupervisionMode = room.Room.SupervisionMode;
        state.ParticipantStatus = response.Status;
        state.SessionStatus = room.Room.SessionState;
        api.SetParticipantToken(response.AccessToken);
        LastJoinOutcome = await CompleteCommittedJoinAsync(SessionAccessMode.LanOnly, ct);
    }

    private async Task EnsureLocalAccountAsync(string expectedServerId, CancellationToken ct)
    {
        if (api.HasTrustedAccountToken) return;
        if (!authState.TryGetTransientCredentials(out var account, out var password))
            throw new LanJoinException(
                "AUTH_REQUIRED",
                "Cần xác thực tài khoản học sinh trên máy giáo viên này. Hãy đăng nhập lại rồi thử tham gia.");

        try
        {
            var login = ApiGuard.Require(await api.PostAsync<AccountLoginRequest, AccountLoginResultDto>(
                "api/v1/auth/login",
                new AccountLoginRequest(
                    account,
                    password,
                    authState.CurrentAccount?.DeviceId ?? Environment.MachineName + "-" + Environment.UserName,
                    Environment.MachineName,
                    ReleaseIdentity.SemanticVersion),
                ct));
            if (login.RequiresStudentConfirmation || string.IsNullOrWhiteSpace(login.AccessToken))
                throw new LanJoinException("AUTH_REQUIRED", "Máy giáo viên yêu cầu xác thực tài khoản học sinh.");
            api.SetAccountToken(login.AccessToken);
            var current = ApiGuard.Require(await api.GetAsync<CurrentAccountDto>("api/v1/auth/me", ct));
            if (current.Role != UserRole.Student
                || authState.CurrentAccount is not { } original
                || string.IsNullOrWhiteSpace(current.ProviderUserId)
                || !string.Equals(
                    current.ProviderUserId,
                    original.ProviderUserId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.Username, original.Username, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.StudentCode, original.StudentCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.OrganizationId, original.OrganizationId, StringComparison.OrdinalIgnoreCase))
            {
                api.SetAccountToken(null);
                throw new LanJoinException(
                    "TOKEN_SERVER_MISMATCH",
                    "Tài khoản trên máy giáo viên không khớp tài khoản học sinh đang đăng nhập.");
            }
            FrontendLogger.LogMessage(
                $"server_id={expectedServerId}; phase=local_account_reauthenticated",
                "StudentConnect.Join");
        }
        catch (BackendApiException ex)
        {
            api.SetAccountToken(null);
            throw new LanJoinException(
                "AUTH_REQUIRED",
                "Không thể xác thực tài khoản học sinh trên máy giáo viên này.",
                ex);
        }
        finally
        {
            password = string.Empty;
        }
    }

    private async Task JoinPublicCloudAsync(CancellationToken ct)
    {
        var requestedCode = RoomCodeRules.Normalize(RoomCode);
        if (!RoomCodeRules.IsValid(requestedCode))
            throw new LanJoinException("ROOM_CODE_INVALID", RoomCodeRules.ValidationMessage);
        if (!publicCloudReady())
        {
            var configurationCode = publicCloudConfigurationError()
                ?? "PUBLICCLOUD_NOT_CONFIGURED";
            throw new LanJoinException(
                configurationCode,
                MapPublicJoinCode(configurationCode).Message);
        }

        Status = "Đang xác minh phòng...";
        var cloudJoin = await joinPublicCloud(requestedCode, ct);
        committedPublicCloudJoin = cloudJoin;
        Status = "Đang gửi yêu cầu tham gia...";
        state.Reset();
        state.ApplyJoin(
            cloudJoin.SessionId,
            cloudJoin.ParticipantId,
            cloudJoin.AccessToken,
            cloudJoin.RoomCode,
            StudentCode.Trim(),
            DisplayName.Trim(),
            SessionAccessMode.PublicCloud);
        state.ExamId = cloudJoin.ExamId;
        state.AdmissionMode = SessionAdmissionMode.OpenRequest;
        state.ExamTitle = cloudJoin.ExamTitle;
        state.Subject = cloudJoin.Subject;
        state.DurationMinutes = cloudJoin.DurationMinutes;
        state.DeliveryType = cloudJoin.DeliveryType;
        state.SupervisionMode = cloudJoin.SupervisionMode;
        state.ResultPolicy = cloudJoin.QuizResultPolicy;
        state.ParticipantStatus = cloudJoin.ParticipantStatus;
        state.SessionStatus = cloudJoin.SessionStatus;
        api.SetParticipantToken(null);
        LastJoinOutcome = await CompleteCommittedJoinAsync(SessionAccessMode.PublicCloud, ct);
    }

    private async Task<StudentJoinOutcome> CompleteCommittedJoinAsync(
        SessionAccessMode mode,
        CancellationToken ct)
    {
        try
        {
            if (mode == SessionAccessMode.PublicCloud)
            {
                var snapshot = committedPublicCloudJoin ?? new(
                    state.SessionId!.Value,
                    state.ExamId ?? Guid.Empty,
                    state.ParticipantId!.Value,
                    state.ParticipantStatus ?? ParticipantStatus.PendingApproval,
                    state.SessionStatus ?? SessionStatus.Waiting,
                    state.RoomCode,
                    state.ExamTitle,
                    state.Subject,
                    state.DurationMinutes,
                    state.DeliveryType,
                    state.SupervisionMode,
                    state.ResultPolicy,
                    null,
                    null,
                    1,
                    state.AccessToken!);
                await completePublicCloudJoin(snapshot, ct);
            }
            else
            {
                await completeLanJoin(ct);
            }

            state.MarkPostJoinSynchronizationSucceeded();
            return new(
                StudentJoinErrorCodes.Succeeded,
                StudentJoinPhase.Completed,
                state.JoinMutationCommitted);
        }
        catch (StudentPostJoinSynchronizationException ex)
        {
            return ReportPostJoinFailure(ex.Outcome, ex);
        }
        catch (Exception ex)
        {
            var outcome = new StudentJoinOutcome(
                StudentJoinErrorCodes.PostJoinSynchronizationFailed,
                StudentJoinPhase.LifecycleResolution,
                state.JoinMutationCommitted,
                StudentJoinErrorCodes.LifecycleResolutionFailed);
            FrontendLogger.Log(ex, "StudentLifecycle.PostJoin.CustomCompletion");
            return ReportPostJoinFailure(outcome, ex);
        }
    }

    private StudentJoinOutcome ReportPostJoinFailure(
        StudentJoinOutcome outcome,
        Exception exception)
    {
        state.MarkPostJoinSynchronizationFailed();
        FrontendLogger.LogMessage(
            $"mode={state.AccessMode}; phase={outcome.Phase}; session_id={state.SessionId}; "
            + $"participant_id={state.ParticipantId}; authority_mutation_committed="
            + $"{(state.JoinMutationCommitted ? "yes" : "no")}; "
            + $"exception_source={exception.GetType().Name}",
            "StudentLifecycle.PostJoin");
        if (!IsDisposed)
        {
            Status = "Đã tham gia phòng, nhưng chưa đồng bộ được trạng thái phòng chờ. "
                + "Ứng dụng sẽ tiếp tục thử đồng bộ. "
                + $"(Mã lỗi: {StudentJoinErrorCodes.PostJoinSynchronizationFailed})";
            StatusTone = "warning";
        }
        return outcome;
    }

    private static async Task CompletePublicCloudJoinAsync(
        ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult _,
        CancellationToken ct)
    {
        var outcome = await AppServices.StudentExamFlow.SynchronizeAfterJoinAsync(
            AppServices.StudentRealtime,
            ct);
        if (!outcome.Succeeded)
            throw new StudentPostJoinSynchronizationException(outcome);
    }

    private static async Task CompleteLanJoinAsync(CancellationToken ct)
    {
        var outcome = await AppServices.StudentExamFlow.SynchronizeAfterJoinAsync(
            AppServices.StudentRealtime,
            ct);
        if (!outcome.Succeeded)
            throw new StudentPostJoinSynchronizationException(outcome);
    }

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(RoomCode))
        {
            ValidationMessage = IsPublicCloudMode
                ? "Nhập mã phòng PublicCloud."
                : "Nhập mã phòng để tham gia trong mạng LAN.";
        }
        else if (!RoomCodeRules.IsValid(RoomCode))
        {
            ValidationMessage = RoomCodeRules.ValidationMessage;
        }
        else if (IsPublicCloudMode && !publicCloudReady())
        {
            ValidationMessage = "PublicCloud chưa được cấu hình hoặc tài khoản chưa đăng nhập.";
        }
        else
        {
            ValidationMessage = string.Empty;
        }
    }

    private void ReportJoinFailure(
        string code,
        string message,
        Exception exception,
        string typedCode)
    {
        LastJoinOutcome = new(
            typedCode,
            StudentJoinPhase.AuthoritativeMutation,
            false,
            code);
        FrontendLogger.Log(exception, $"StudentConnect.Join.{code}");
        FrontendLogger.LogMessage(
            $"mode={SelectedAccessMode}; phase={StudentJoinPhase.AuthoritativeMutation}; "
            + $"session_id={state.SessionId}; participant_id={state.ParticipantId}; "
            + $"authority_mutation_committed=no; exception_source={exception.GetType().Name}; "
            + $"typed_error={typedCode}; cause={code}; room={MaskRoomCode(RoomCode)}",
            "StudentConnect.Join");
        Status = $"{message} (Mã lỗi: {code})";
        StatusTone = "danger";
    }

    private static string ClassifyJoinFailure(string code) => code switch
    {
        "AUTH_REQUIRED" or "UNAUTHORIZED" or "FORBIDDEN"
            or "PUBLICCLOUD_AUTH_EXPIRED" or "PUBLICCLOUD_AUTH_INVALID" =>
            StudentJoinErrorCodes.AuthenticationRequired,
        "DISCOVERY_TIMEOUT" or "ROOM_NOT_WAITING" or "OPEN_PUBLIC_SESSION_NOT_FOUND"
            or "NOT_FOUND" => StudentJoinErrorCodes.RoomNotFound,
        "PARTICIPANT_REJECTED" => StudentJoinErrorCodes.ParticipantRejected,
        _ => StudentJoinErrorCodes.JoinMutationFailed
    };

    private static string MapBackendCode(string code) => code switch
    {
        "NOT_FOUND" => "ROOM_NOT_WAITING",
        "INVALID_STATE_TRANSITION" => "ROOM_NOT_ACCEPTING",
        "UNAUTHORIZED" or "FORBIDDEN" => "AUTH_REQUIRED",
        _ => "JOIN_REJECTED"
    };

    internal static PublicJoinErrorPresentation MapPublicJoinError(Exception exception) =>
        exception switch
        {
            ExamTransfer.Desktop.Infrastructure.PublicCloudApiException apiException =>
                MapPublicJoinCode(apiException.Code),
            OperationCanceledException or TimeoutException =>
                new(
                    "PUBLICCLOUD_TIMEOUT",
                    "PublicCloud không phản hồi trong thời gian cho phép. "
                    + "Hãy kiểm tra kết nối và thử lại.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            HttpRequestException =>
                new(
                    "PUBLICCLOUD_NETWORK_UNAVAILABLE",
                    "Không thể kết nối tới PublicCloud. "
                    + "Hãy kiểm tra kết nối mạng và thử lại.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            _ =>
                new(
                    "PUBLICCLOUD_UNKNOWN_ERROR",
                    "Không thể hoàn tất yêu cầu PublicCloud.",
                    StudentJoinErrorCodes.JoinMutationFailed)
        };

    private static PublicJoinErrorPresentation MapPublicJoinCode(string code)
    {
        var normalizedCode = string.IsNullOrWhiteSpace(code)
            ? "PUBLICCLOUD_UNKNOWN_ERROR"
            : code.Trim();
        return normalizedCode switch
        {
            "P0003" or "OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS" =>
                new(
                    normalizedCode,
                    "Mã phòng đang bị trùng trên hệ thống.\n"
                    + "Giáo viên cần đóng phòng cũ hoặc tạo mã phòng mới.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            "OPEN_PUBLIC_SESSION_NOT_FOUND" or "NOT_FOUND" =>
                new(
                    normalizedCode,
                    "Không tìm thấy phòng PublicCloud. Hãy kiểm tra mã phòng và thử lại.",
                    StudentJoinErrorCodes.RoomNotFound),
            "PUBLICCLOUD_AUTH_EXPIRED" or "PUBLICCLOUD_AUTH_INVALID"
                or "AUTHENTICATION_REQUIRED" =>
                new(
                    normalizedCode,
                    "Phiên đăng nhập PublicCloud đã hết hạn; hãy đăng nhập lại.",
                    StudentJoinErrorCodes.AuthenticationRequired),
            "PUBLICCLOUD_NOT_CONFIGURED" =>
                new(
                    normalizedCode,
                    "PublicCloud chưa được cấu hình trên bản cài đặt này.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            "PUBLICCLOUD_INVALID_URL" =>
                new(
                    normalizedCode,
                    "Địa chỉ PublicCloud không hợp lệ hoặc không dùng HTTPS.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            "PUBLICCLOUD_INVALID_PUBLISHABLE_KEY" =>
                new(
                    normalizedCode,
                    "Publishable key PublicCloud không hợp lệ.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            "PUBLICCLOUD_SCHEMA_INCOMPATIBLE" =>
                new(
                    normalizedCode,
                    "PublicCloud chưa đạt schema capability 23.",
                    StudentJoinErrorCodes.JoinMutationFailed),
            _ =>
                new(
                    normalizedCode,
                    "Không thể hoàn tất yêu cầu PublicCloud.",
                    StudentJoinErrorCodes.JoinMutationFailed)
        };
    }

    private static string MaskRoomCode(string roomCode)
    {
        if (roomCode.Length <= 2) return new string('*', roomCode.Length);
        return $"{roomCode[0]}{new string('*', roomCode.Length - 2)}{roomCode[^1]}";
    }

    private void ApplyLocalSessionSnapshot(SessionDetailDto detail, ParticipantStatus participantStatus)
    {
        state.ExamId = detail.Summary.ExamId;
        state.AdmissionMode = detail.Summary.AdmissionMode;
        state.ExamTitle = detail.Summary.Title;
        state.DeliveryType = detail.Summary.DeliveryType;
        state.SupervisionMode = detail.Summary.SupervisionMode;
        state.ResultPolicy = detail.Summary.QuizResultPolicy;
        state.ParticipantStatus = participantStatus;
        state.SessionStatus = detail.Summary.Status;
    }

    protected override void RaiseCommands()
    {
        (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (JoinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

internal sealed record PublicJoinErrorPresentation(
    string Code,
    string Message,
    string TypedCode);

public sealed record OpenRoomCard(OpenSessionDiscoveryDto Room)
{
    public Guid SessionId => Room.SessionId;
    public string RoomCode => Room.RoomCode;
    public string RoomName => Room.RoomName;
    public string? ClassName => Room.ClassName;
    public string ClassDisplay => string.IsNullOrWhiteSpace(Room.ClassCode) ? Room.ClassName ?? "Chưa gắn lớp" : $"{Room.ClassName} ({Room.ClassCode})";
    public string ExamTitle => Room.ExamTitle;
    public string TeacherName => Room.TeacherName;
    public string BaseAddress => Room.BaseAddress;
    public string ApprovalText => Room.RequireApproval ? "Cần giáo viên duyệt" : "Tự động duyệt";
    public string CapacityText => Room.Capacity.HasValue ? $"{Room.CurrentParticipantCount}/{Room.Capacity}" : $"{Room.CurrentParticipantCount} học sinh";
    public string StartText => Room.ScheduledStartUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "Chưa đặt giờ";
    public string Subject => Room.Subject;
    public string DurationText => Room.DurationMinutes > 0 ? $"{Room.DurationMinutes} phút" : "Chưa rõ thời lượng";
    public string DeliveryText => Room.DeliveryType == ExamDeliveryType.MultipleChoice ? "Trắc nghiệm" : "Nộp file";
    public string StatusText => Room.SessionState == SessionStatus.Waiting ? "Đang chờ" : Room.SessionState.ToString();
}

public static class StudentConnectJoinRouter
{
    public static Task DispatchAsync(
        SessionAccessMode mode,
        Func<CancellationToken, Task> lan,
        Func<CancellationToken, Task> publicCloud,
        CancellationToken cancellationToken) =>
        mode switch
        {
            SessionAccessMode.LanOnly => lan(cancellationToken),
            SessionAccessMode.PublicCloud => publicCloud(cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported session access mode: {mode}.")
        };
}

public sealed class LanJoinException : Exception
{
    public LanJoinException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

public sealed class StudentPostJoinSynchronizationException(StudentJoinOutcome outcome)
    : Exception(outcome.CauseCode ?? outcome.Code)
{
    public StudentJoinOutcome Outcome { get; } = outcome;
}

public sealed record ServerCard(string Name, string Teacher, string Ip, int Port, int LatencyMs, string Status, string Tone, int Connected, int Capacity, string Fingerprint, string Version)
{
    public string Address => $"{Ip}:{Port}";
    public string CapacityText => Capacity <= 0 ? "Sẵn sàng kết nối" : $"{Connected}/{Capacity} thiết bị";
}

public sealed record ReadinessItem(string Title, string Description, bool Ready);
