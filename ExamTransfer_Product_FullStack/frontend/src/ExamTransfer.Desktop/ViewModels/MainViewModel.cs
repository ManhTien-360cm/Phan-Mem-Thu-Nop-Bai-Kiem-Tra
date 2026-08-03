using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IBackendClient api;
    private readonly AppAuthSessionState authState;
    private readonly StudentRealtimeNotificationRouter studentRealtimeRouter;
    private CancellationTokenSource? accountHeartbeatCts;
    private AppMode mode;
    private NavigationItem? selected;
    private string connection = "Đang kiểm tra kết nối";
    private string connectionTone = "warning";
    private object? page;
    private bool isBuildingNavigation;
    private bool isNavigating;
    private int pendingSubmissionCount;

    public MainViewModel()
    {
        api = AppServices.Backend;
        authState = AppServices.AuthState;
        studentRealtimeRouter = new(
            new StudentRealtimeNotificationAdapter(),
            AppServices.Notifications,
            RefreshStudentRealtimeStateAsync);
        RefreshCommand = new AsyncRelayCommand(CheckAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => authState.IsAuthenticated);
        pendingSubmissionCount = AppServices.SubmissionRecovery.PendingCount;
        AppServices.SubmissionRecovery.PendingCountChanged += OnPendingSubmissionCountChanged;
        AppServices.StudentExamFlow.NavigationRequested += OnStudentExamNavigationRequested;
        AppServices.StudentRealtime.EventReceived += OnStudentRealtimeEvent;
        AppServices.StudentRealtime.NotificationReceived += OnStudentRealtimeNotification;
        AppServices.PublicCloud.SessionChanged += OnPublicCloudSessionChanged;
        CurrentPage = CreateLoginPage();
        FrontendLogger.SetContext("Login", "Auth");
        RestoreAuthAsync().SafeFireAndForget("MainViewModel.RestoreAuthAsync");
    }

    public ObservableCollection<NavigationItem> Navigation { get; } = new();

    public AppMode Mode
    {
        get => mode;
        private set
        {
            if (!Set(ref mode, value)) return;
            FrontendLogger.SetContext(mode.ToString());
            Raise(nameof(ModeTitle));
            Raise(nameof(IsTeacherMode));
            Raise(nameof(IsStudentMode));
            BuildNavigation();
        }
    }

    public bool IsTeacherMode => Mode == AppMode.Teacher;
    public bool IsStudentMode => Mode == AppMode.Student;
    public bool IsAuthenticated => authState.IsAuthenticated;
    public string AccountDisplay => authState.DisplayName;
    public string AccountRole => authState.RoleLabel;
    public string SessionStatusText => authState.IsAuthenticated ? "Phiên tài khoản đang hoạt động" : "Chưa đăng nhập";
    public string PendingSubmissionText => pendingSubmissionCount > 0 ? $"{pendingSubmissionCount} bài đã lưu đang chờ gửi" : string.Empty;

    public string ModeTitle => !authState.IsAuthenticated
        ? "Đăng nhập"
        : Mode switch
        {
            AppMode.Teacher => "Không gian giáo viên",
            AppMode.Student => "Không gian học sinh",
            _ => "ExamTransfer"
        };

    public string Connection { get => connection; private set => Set(ref connection, value); }
    public string ConnectionTone { get => connectionTone; private set => Set(ref connectionTone, value); }
    public string ThemeLabel => ThemeManager.CurrentLabel;

    public object? CurrentPage
    {
        get => page;
        private set
        {
            if (ReferenceEquals(page, value)) return;
            var previous = page;
            Set(ref page, value);
            DisposePage(previous);
        }
    }

    public NavigationItem? Selected
    {
        get => selected;
        set
        {
            if (isBuildingNavigation)
            {
                Set(ref selected, value);
                return;
            }

            if (Set(ref selected, value) && value is not null)
                NavigateSafely(value);
        }
    }

    public string PageTitle => CurrentPage is ChangePasswordViewModel
        ? "Đổi mật khẩu bắt buộc"
        : Selected?.Title ?? (authState.IsAuthenticated ? "ExamTransfer" : "Đăng nhập");
    public string PageDescription => CurrentPage is ChangePasswordViewModel
        ? "Hoàn tất bảo mật tài khoản trước khi sử dụng chức năng học sinh"
        : Selected?.Description ?? (authState.IsAuthenticated ? "Thu và gửi bài thi an toàn trong mạng LAN" : "Sử dụng tài khoản được cấp sẵn");
    public string PageGlyph => CurrentPage is ChangePasswordViewModel ? "\uE72E" : Selected?.Glyph ?? "\uE8A5";
    public string EnvironmentLabel => "Local-first LAN";

    public ICommand RefreshCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand LogoutCommand { get; }

    private void OnPendingSubmissionCountChanged(object? sender, int count)
    {
        void Apply()
        {
            pendingSubmissionCount = count;
            Raise(nameof(PendingSubmissionText));
        }
        if (Application.Current?.Dispatcher?.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    private void OnStudentExamNavigationRequested(object? sender, StudentExamNavigationRequest request)
    {
        void Navigate()
        {
            var target = Navigation.FirstOrDefault(x => x.Key == request.Resolution.RouteKey);
            if (target is null || Selected?.Key == target.Key)
                return;
            Selected = target;
        }
        if (Application.Current?.Dispatcher?.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Navigate);
        else
            Navigate();
    }

    private void OnStudentRealtimeEvent(object? sender, string eventName)
    {
        if (AppServices.StudentState.AccessMode == SessionAccessMode.LanOnly
            && StudentRealtimeNotificationAdapter.IsSupportedEventName(eventName))
            return;
        if (!StudentExamFlowCoordinator.IsLifecycleProgressionEvent(eventName))
            return;
        ResolveStudentLifecycleAsync().SafeFireAndForget("MainViewModel.StudentLifecycle.Event");
    }

    private void OnStudentRealtimeNotification(
        object? sender,
        StudentRealtimeNotification notification)
    {
        var state = AppServices.StudentState;
        if (!state.SessionId.HasValue || !state.ParticipantId.HasValue)
            return;
        studentRealtimeRouter.RouteAsync(
                notification,
                new(
                    state.SessionId.Value,
                    state.ParticipantId.Value,
                    state.Revision,
                    state.AccessMode),
                CancellationToken.None)
            .SafeFireAndForget("MainViewModel.StudentRealtimeNotification");
    }

    private async Task ResolveStudentLifecycleAsync(CancellationToken cancellationToken = default)
    {
        if (!authState.IsStudent || !AppServices.StudentState.HasSession)
            return;
        _ = await AppServices.StudentExamFlow.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            cancellationToken);
    }

    private async Task RefreshStudentRealtimeStateAsync(
        StudentRealtimeRoute route,
        CancellationToken cancellationToken)
    {
        await ResolveStudentLifecycleAsync(cancellationToken);
        if (route.RefreshTarget != StudentRealtimeRefreshTarget.Results
            || !IsQuizResultEvent(route.SourceEventName)
            || CurrentPage is not StudentQuizViewModel quiz)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher
                .InvokeAsync(() => quiz.RefreshAuthoritativeReviewAsync(cancellationToken))
                .Task
                .Unwrap();
            return;
        }
        await quiz.RefreshAuthoritativeReviewAsync(cancellationToken);
    }

    private static bool IsQuizResultEvent(string eventName) =>
        eventName.StartsWith(RealtimeEvents.QuizGradeReturned, StringComparison.Ordinal)
        || eventName.StartsWith(RealtimeEvents.QuizGradeReopened, StringComparison.Ordinal);

    private async Task RestoreAuthAsync()
    {
        if (!authState.TryRestoreAuthenticatedSession(out var restored))
            return;
        try
        {
            var configuredOrganization =
                AppServices.PublicCloudOptions.Get().OrganizationId;
            if (configuredOrganization is null
                || !Guid.TryParse(
                    restored.Account.OrganizationId,
                    out var restoredOrganization)
                || restoredOrganization != configuredOrganization.Value)
                throw new InvalidOperationException(
                    "RESTORED_AUTH_ORGANIZATION_MISMATCH");

            CurrentAccountDto current;
            var effectiveAccessToken = restored.AccessToken;
            var effectiveRefreshToken = restored.RefreshToken;
            if (restored.Authority == AuthSessionAuthority.Supabase)
            {
                if (restored.Account.Role != UserRole.Student
                    || string.IsNullOrWhiteSpace(restored.Account.ProviderUserId)
                    || !AppServices.PublicCloud.TryRestoreSession(
                        restored.AccessToken,
                        restored.RefreshToken,
                        restored.Account.ProviderUserId,
                        restored.Account.ExpiresAtUtc))
                    throw new InvalidOperationException("OFFLINE_AUTH_CACHE_INVALID");
                api.SetAccountToken(null);
                var cloudSession = await AppServices.PublicCloud
                    .RestoreAuthenticatedAccountAsync(
                        restored.Account.DeviceId,
                        CancellationToken.None);
                current = cloudSession.Account;
                effectiveAccessToken = cloudSession.AccessToken;
                effectiveRefreshToken = cloudSession.RefreshToken;
            }
            else
            {
                if (restored.Account.Role is not (UserRole.Teacher or UserRole.Admin))
                    throw new InvalidOperationException(ErrorCodes.AuthenticatedRoleInvalid);
                var lifecycle = await AppServices.LocalServerLifecycle.EnsureStartedAsync(
                    restored.Account.Role);
                if (lifecycle.Status is not ("SERVER_HEALTHY" or "SERVER_STARTED"))
                    throw new InvalidOperationException(lifecycle.Status);
                api.SetAccountToken(restored.AccessToken);
                current = ApiGuard.Require(await api.GetAsync<CurrentAccountDto>("api/v1/auth/me"));
                if (current.UserId != restored.Account.UserId
                    || current.Role != restored.Account.Role
                    || !string.Equals(
                        current.OrganizationId,
                        restored.Account.OrganizationId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        current.ProviderUserId,
                        restored.Account.ProviderUserId,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RESTORED_AUTH_PROFILE_MISMATCH");
            }

            await RunOnUiAsync(() =>
            {
                authState.SetAuthenticated(
                    current,
                    effectiveAccessToken,
                    restored.Authority,
                    effectiveRefreshToken);
                CompleteAuthenticatedShell();
            });
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "MainViewModel.RestoreAuthAsync");
            await RunOnUiAsync(() => ClearAuthToLogin());
        }
    }

    private async Task OnAuthenticatedAsync()
    {
        CompleteAuthenticatedShell();
        await CheckAsync();
    }

    private async Task OnPasswordChangedAsync()
    {
        CompleteAuthenticatedShell();
        await CheckAsync();
    }

    private void CompleteAuthenticatedShell()
    {
        RaiseAuthProperties();
        var targetMode = authState.CurrentAccount?.Role switch
        {
            UserRole.Student => AppMode.Student,
            UserRole.Teacher or UserRole.Admin => AppMode.Teacher,
            _ => throw new InvalidOperationException(ErrorCodes.AuthenticatedRoleInvalid)
        };
        if (Mode != targetMode) Mode = targetMode;
        else BuildNavigation();
        StartAccountHeartbeat();
    }

    private async Task LogoutAsync()
    {
        var localServerRole = authState.IsTeacher
            ? authState.CurrentAccount!.Role
            : (UserRole?)null;
        try
        {
            if (authState.IsStudent && AppServices.PublicCloud.Authenticated)
                await AppServices.PublicCloud.LogoutAsync();

            var deviceId = authState.CurrentAccount?.DeviceId;
            if (api.HasTrustedAccountToken && !string.IsNullOrWhiteSpace(deviceId))
            {
                _ = await api.PostAsync<LogoutRequest, object>(
                    "api/v1/auth/logout",
                    new LogoutRequest(deviceId, "frontend_logout"));
            }
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "MainViewModel.LogoutAsync");
        }
        finally
        {
            if (localServerRole.HasValue)
            {
                try
                {
                    await AppServices.LocalServerLifecycle
                        .StopExistingPackagedServerAsync(localServerRole.Value);
                }
                catch (Exception ex)
                {
                    FrontendLogger.Log(ex, "MainViewModel.StopLocalServer");
                }
            }
            ClearAuthToLogin();
        }
    }

    private void StartAccountHeartbeat()
    {
        accountHeartbeatCts?.Cancel();
        accountHeartbeatCts?.Dispose();
        if (!authState.IsTeacher || authState.CurrentAccount is null) return;

        accountHeartbeatCts = new CancellationTokenSource();
        var token = accountHeartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (authState.CurrentAccount is not { } account) continue;
                    var response = await api.PostAsync<AccountHeartbeatRequest, AccountHeartbeatResponse>(
                        "api/v1/auth/heartbeat",
                        new AccountHeartbeatRequest(account.DeviceId, Environment.MachineName, DateTimeOffset.UtcNow),
                        token);
                    _ = ApiGuard.Require(response);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    FrontendLogger.Log(ex, "MainViewModel.AccountHeartbeat");
                    await RunOnUiAsync(() => ClearAuthToLogin());
                    break;
                }
            }
        }, token);
    }

    private void ClearAuthToLogin()
    {
        accountHeartbeatCts?.Cancel();
        accountHeartbeatCts?.Dispose();
        accountHeartbeatCts = null;
        authState.Clear();
        AppServices.StudentRealtime.StopAsync().SafeFireAndForget("StudentRealtime.Logout");
        AppServices.PublicCloud.Logout();
        AppServices.LocalServerLifecycle
            .StopOwnedAsync()
            .SafeFireAndForget("LocalServer.StopOwned");
        AppServices.StudentState.Reset();
        api.SetAccountToken(null);
        api.SetParticipantToken(null);
        Navigation.Clear();
        Set(ref selected, null, nameof(Selected));
        mode = AppMode.None;
        CurrentPage = CreateLoginPage();
        RaiseAuthProperties();
        RaisePageProperties();
    }

    private LoginViewModel CreateLoginPage() => new(api, authState, OnAuthenticatedAsync);

    private void OnPublicCloudSessionChanged(
        object? sender,
        ExamTransfer.Desktop.Infrastructure.SupabaseAuthSessionChangedEventArgs session)
    {
        if (!authState.IsStudent)
            return;

        void Persist()
        {
            if (!authState.UpdateSupabaseSession(
                    session.AccessToken,
                    session.RefreshToken,
                    session.ExpiresAtUtc))
                FrontendLogger.LogMessage(
                    "Supabase refresh session was rejected by the local identity binding.",
                    "MainViewModel.PublicCloudSessionChanged");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Persist);
        else
            Persist();
    }

    private ChangePasswordViewModel CreatePasswordChangePage() =>
        new(api, authState, OnPasswordChangedAsync);

    private async Task CheckAsync()
    {
        await RunOnUiAsync(() =>
        {
            Connection = "Đang kiểm tra kết nối";
            ConnectionTone = "warning";
        });

        try
        {
            if (authState.IsStudent)
            {
                if (!AppServices.PublicCloud.Configured
                    || !AppServices.PublicCloud.Authenticated)
                    throw new InvalidOperationException("PublicCloud chưa có phiên xác thực.");
                await AppServices.PublicCloud.EnsureSchemaCompatibleAsync(CancellationToken.None);
                await RunOnUiAsync(() =>
                {
                    Connection = "PublicCloud sẵn sàng";
                    ConnectionTone = "success";
                });
                return;
            }

            if (!authState.IsTeacher)
                throw new InvalidOperationException("Chưa có tài khoản đã xác thực.");
            var response = await api.GetSystemStatusAsync();
            await RunOnUiAsync(() =>
            {
                if (response?.Success == true)
                {
                    Connection = "Máy chủ sẵn sàng";
                    ConnectionTone = "success";
                }
                else
                {
                    Connection = "Máy chủ chưa phản hồi";
                    ConnectionTone = "warning";
                }
            });
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "MainViewModel.CheckAsync");
            await RunOnUiAsync(() =>
            {
                Connection = "Đang làm việc ngoại tuyến";
                ConnectionTone = "warning";
            });
        }
    }

    private void ToggleTheme()
    {
        ThemeManager.Toggle();
        Raise(nameof(ThemeLabel));
    }

    private void BuildNavigation()
    {
        if (isBuildingNavigation) return;

        try
        {
            isBuildingNavigation = true;
            Navigation.Clear();

            if (!authState.IsAuthenticated)
            {
                Set(ref selected, null, nameof(Selected));
                CurrentPage = CreateLoginPage();
                return;
            }

            if (authState.IsStudent && authState.MustChangePassword)
            {
                Set(ref selected, null, nameof(Selected));
                CurrentPage = CreatePasswordChangePage();
                return;
            }

            var items = authState.CurrentAccount?.Role switch
            {
                UserRole.Student => StudentItems(),
                UserRole.Teacher or UserRole.Admin => TeacherItems(),
                _ => throw new InvalidOperationException(
                    ErrorCodes.AuthenticatedRoleInvalid)
            };
            foreach (var item in items) Navigation.Add(item);

            var first = Navigation.FirstOrDefault();
            Set(ref selected, first, nameof(Selected));
            if (first is not null) NavigateSafely(first);
        }
        finally
        {
            isBuildingNavigation = false;
            RaisePageProperties();
        }
    }

    private object CreatePage(NavigationItem item) =>
        item.Key switch
        {
            "T-01" => new DashboardViewModel(api),
            "T-02" => new ClassManagementViewModel(api),
            "T-05" => new ExamManagementViewModel(api),
            "T-08" => new SessionManagementViewModel(api),
            "T-10" => new LobbyViewModel(api),
            "T-11" => new LiveMonitorViewModel(api),
            "T-12" => new SubmissionCenterViewModel(api),
            "T-14" => new ExportCenterViewModel(api),
            "T-15" => new GradingCenterViewModel(api),
            "T-17" => new ControlCenterViewModel(api),
            "T-18" => new HistoryAuditViewModel(api),
            "T-20" => new BackupCenterViewModel(api),
            "T-21" => new SettingsPageViewModel(api),
            "S-00" => new StudentHomeViewModel(authState),
            "S-01" => new StudentConnectViewModel(api, AppServices.StudentState, authState),
            "S-03" => new StudentWaitingViewModel(api, AppServices.StudentState, authState),
            "S-04" => new StudentExamViewModel(api, AppServices.StudentState),
            "S-05" => new StudentDownloadViewModel(api, AppServices.StudentState),
            "S-06" => new StudentQuizViewModel(api, AppServices.StudentState),
            "S-07" => new StudentSubmissionViewModel(api, AppServices.StudentState, authState),
            "S-08" => new StudentReceiptViewModel(api, AppServices.StudentState),
            "S-09" => new StudentHistoryViewModel(AppServices.StudentState),
            "S-10" => new StudentSettingsViewModel(AppServices.Preferences),
            "S-11" => new StudentResultsViewModel(
                AppServices.StudentResults,
                authState,
                AppServices.StudentState,
                AppServices.StudentRealtime,
                AppServices.Folders),
            _ => new ErrorPageViewModel("Màn hình chưa được ánh xạ.", item.Key, FrontendLogger.LogPath, RetrySelected, GoHome)
        };

    private void NavigateSafely(NavigationItem item)
    {
        if (isNavigating) return;

        object? nextPage = null;
        try
        {
            isNavigating = true;
            FrontendLogger.SetContext(Mode.ToString(), item.Key);
            nextPage = CreatePage(item);
            var previous = page;
            SetCurrentPageWithoutDisposing(nextPage);
            DisposePage(previous);

            if (nextPage is IAsyncInitializable initializable)
                initializable.InitializeAsync(CancellationToken.None).SafeFireAndForget($"{nextPage.GetType().Name}.InitializeAsync");
        }
        catch (Exception ex)
        {
            DisposePage(nextPage);
            var traceId = FrontendLogger.Log(ex, $"MainViewModel.NavigateSafely:{item.Key}");
            CurrentPage = CreateErrorPage("Không thể mở màn hình này. Ứng dụng vẫn đang chạy và lỗi đã được ghi log.", traceId);
        }
        finally
        {
            isNavigating = false;
        }

        RaisePageProperties();
    }

    private void SetCurrentPageWithoutDisposing(object? value)
    {
        if (!ReferenceEquals(page, value))
            Set(ref page, value, nameof(CurrentPage));
    }

    private static void DisposePage(object? value)
    {
        if (value is IDisposable disposable) disposable.Dispose();
    }

    private ErrorPageViewModel CreateErrorPage(string message, string traceId) =>
        new(message, traceId, FrontendLogger.LogPath, RetrySelected, GoHome);

    private void RetrySelected()
    {
        if (Selected is { } item) NavigateSafely(item);
    }

    private void GoHome()
    {
        if (!authState.IsAuthenticated)
        {
            ClearAuthToLogin();
            return;
        }

        BuildNavigation();
    }

    private void RaiseAuthProperties()
    {
        Raise(nameof(IsAuthenticated));
        Raise(nameof(AccountDisplay));
        Raise(nameof(AccountRole));
        Raise(nameof(SessionStatusText));
        Raise(nameof(ModeTitle));
        (LogoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaisePageProperties()
    {
        Raise(nameof(PageTitle));
        Raise(nameof(PageDescription));
        Raise(nameof(PageGlyph));
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static IReadOnlyList<NavigationItem> TeacherItems() =>
        new NavigationItem[]
        {
            new("T-01", "Tổng quan", "Tổng quan", "Thống kê, cảnh báo và phiên đang vận hành", "\uE80F"),
            new("T-02", "Lớp học", "Quản lý", "Lớp, học sinh, import CSV/Excel", "\uE716"),
            new("T-05", "Bài kiểm tra", "Quản lý", "Đề thi, file đề, phiên bản và quy định", "\uE8A5"),
            new("T-08", "Phòng thi", "Vận hành", "Tạo, cấu hình và mở phòng thi", "\uE7BE"),
            new("T-10", "Phòng chờ", "Vận hành", "Duyệt học sinh và kiểm tra sẵn sàng", "\uE77B"),
            new("T-11", "Giám sát trực tiếp", "Vận hành", "Theo dõi realtime toàn bộ học sinh", "\uE9D2"),
            new("T-12", "Thu bài", "Kết quả", "Bài nộp, attempt, biên nhận và nộp lại", "\uE896"),
            new("T-14", "Xuất dữ liệu", "Kết quả", "ZIP, Excel/CSV, manifest và audit", "\uEDE1"),
            new("T-15", "Chấm bài", "Nâng cao", "Điểm, rubric, nhận xét và trả kết quả", "\uE70B"),
            new("T-17", "Kiểm soát thi", "Nâng cao", "Policy, agent, vi phạm và xử lý", "\uE72E"),
            new("T-18", "Lịch sử & Audit", "Hệ thống", "Lịch sử phiên và nhật ký bất biến", "\uE81C"),
            new("T-20", "Sao lưu", "Hệ thống", "Backup, checksum và khôi phục", "\uE753"),
            new("T-21", "Cài đặt", "Hệ thống", "Mạng, lưu trữ, cloud và bảo mật", "\uE713")
        };

    private static IReadOnlyList<NavigationItem> StudentItems() =>
        new NavigationItem[]
        {
            new("S-00", "Trang chủ", "Tài khoản", "Thông tin sinh viên và trạng thái phiên đăng nhập", "\uE80F"),
            new("S-01", "Kết nối phòng", "Tham gia", "Quét LAN, nhập IP/cổng hoặc mã phòng", "\uE968"),
            new("S-03", "Phòng chờ", "Tham gia", "Chờ duyệt và kiểm tra sẵn sàng", "\uE823"),
            new("S-04", "Kỳ thi hiện tại", "Làm bài", "Đồng hồ server và tiến trình làm bài", "\uE916"),
            new("S-05", "Nhận đề", "Làm bài", "Tải file, resume và xác minh SHA-256", "\uE896"),
            new("S-06", "Thi trắc nghiệm", "Làm bài", "Lưu cục bộ, đồng bộ và chấm tự động", "\uE8D4"),
            new("S-07", "Nộp bài", "Làm bài", "Chunk upload, resume, finalize và xác nhận", "\uE898"),
            new("S-08", "Biên nhận", "Kết quả", "Mã biên nhận, thời gian server và hash", "\uF0E3"),
            new("S-09", "Lịch sử cục bộ", "Kết quả", "Các phiên và bài đã nộp trên máy", "\uE81C"),
            new("S-11", "Kết quả đã trả", "Kết quả", "Điểm, nhận xét và attachment chính thức", "\uE9D9"),
            new("S-10", "Cài đặt", "Hệ thống", "Profile, mạng, thư mục, thông báo và log", "\uE713")
        };

    public void Dispose()
    {
        accountHeartbeatCts?.Cancel();
        accountHeartbeatCts?.Dispose();
        AppServices.SubmissionRecovery.PendingCountChanged -= OnPendingSubmissionCountChanged;
        AppServices.StudentExamFlow.NavigationRequested -= OnStudentExamNavigationRequested;
        AppServices.StudentRealtime.EventReceived -= OnStudentRealtimeEvent;
        AppServices.StudentRealtime.NotificationReceived -= OnStudentRealtimeNotification;
        AppServices.PublicCloud.SessionChanged -= OnPublicCloudSessionChanged;
        DisposePage(CurrentPage);
    }
}

public static class AppServices
{
    public static string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("EXAMTRANSFER_API") ?? "http://localhost:5048";

    public static IFileDialogService Files { get; } = new FileDialogService();
    public static IFolderDialogService Folders { get; } = new FolderDialogService();
    public static IDialogService Dialogs { get; } = new DialogService();
    public static IClipboardService Clipboard { get; } = new ClipboardService();
    public static IToastService Toasts { get; } = new ToastService();
    public static INotificationCenter Notifications { get; } = new NotificationCenter();
    public static ILocalPreferenceService Preferences { get; } = new LocalPreferenceService();
    public static AppAuthSessionState AuthState { get; } = new();
    public static StudentSessionState StudentState { get; } = new();
    public static IServerClock ServerClock { get; } = new ServerClock();
    public static ICountdownTickerFactory CountdownTickers { get; } = new DispatcherCountdownTickerFactory();
    public static ExamTransfer.Desktop.Infrastructure.IPublicCloudRuntimeOptionsProvider PublicCloudOptions { get; } =
        new ExamTransfer.Desktop.Infrastructure.PublicCloudRuntimeOptionsProvider();
    public static ExamTransfer.Desktop.Infrastructure.SupabasePublicCloudClient PublicCloud { get; } =
        new(serverClock: ServerClock, optionsProvider: PublicCloudOptions);
    public static ExamTransfer.Desktop.Infrastructure.SupabaseRealtimeService PublicRealtime { get; } =
        new(PublicCloudOptions);
    public static ILanDiscoveryService LanDiscovery { get; } =
        new ExamTransfer.Desktop.Infrastructure.LanDiscoveryService();
    public static ExamTransfer.Desktop.Infrastructure.LocalServerLifecycleService LocalServerLifecycle { get; } =
        new();

    public static IBackendClient Backend { get; } =
        new ExamTransfer.Desktop.Infrastructure.BackendClient(BaseUrl);
    public static IStudentResultsService StudentResults { get; } =
        new StudentResultsService(Backend, PublicCloud, StudentState);
    public static IUnifiedAuthenticationService Authentication { get; } =
        new UnifiedAuthenticationService(Backend, PublicCloud, LocalServerLifecycle);
    public static IStudentExamFlowCoordinator StudentExamFlow { get; } =
        new StudentExamFlowCoordinator(Backend, PublicCloud, StudentState, Toasts);
    public static IStudentHeartbeatService StudentHeartbeat { get; } =
        new ExamTransfer.Desktop.Infrastructure.StudentHeartbeatService(Backend, StudentState, ServerClock);
    public static IStudentRealtimeService StudentRealtime { get; } =
        new ExamTransfer.Desktop.Infrastructure.StudentRealtimeService(
            Backend,
            StudentState,
            PublicRealtime,
            PublicCloud);
    public static ISubmissionRecoveryService SubmissionRecovery { get; } =
        new ExamTransfer.Desktop.Infrastructure.SubmissionRecoveryService(AuthState, StudentState, LanDiscovery);
}
