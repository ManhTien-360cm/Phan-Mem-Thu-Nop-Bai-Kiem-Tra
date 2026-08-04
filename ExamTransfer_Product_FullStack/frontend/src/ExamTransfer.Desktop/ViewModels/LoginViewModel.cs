using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly IBackendClient api;
    private readonly AppAuthSessionState authState;
    private readonly Func<Task> authenticated;
    private readonly IUnifiedAuthenticationService authentication;
    private string account = string.Empty;
    private string password = string.Empty;
    private bool isBusy;
    private string status = "Học sinh nhập mã sinh viên; giáo viên và quản trị viên nhập email.";
    private string statusTone = "info";

    public LoginViewModel(
        IBackendClient api,
        AppAuthSessionState authState,
        Func<Task> authenticated,
        IUnifiedAuthenticationService? authentication = null)
    {
        this.api = api;
        this.authState = authState;
        this.authenticated = authenticated;
        this.authentication = authentication ?? AppServices.Authentication;
        DeviceId = EnsureDeviceId();
        LoginCommand = new AsyncRelayCommand(LoginAsync, CanLogin);
    }

    public string Account
    {
        get => account;
        set
        {
            if (Set(ref account, value))
                RaiseCommand();
        }
    }

    public string Password
    {
        get => password;
        set
        {
            if (Set(ref password, value))
                RaiseCommand();
        }
    }

    public string DeviceId { get; }
    public string VersionLabel => $"v{ReleaseIdentity.SemanticVersion}";

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value))
                RaiseCommand();
        }
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public string StatusTone
    {
        get => statusTone;
        private set => Set(ref statusTone, value);
    }

    public ICommand LoginCommand { get; }

    private bool CanLogin() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Account)
        && !string.IsNullOrWhiteSpace(Password);

    private async Task LoginAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            Status = "Đang xác thực tài khoản...";
            StatusTone = "primary";
            authState.Clear();
            api.SetAccountToken(null);
            api.SetParticipantToken(null);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await authentication.LoginAsync(
                Account.Trim(),
                Password,
                DeviceId,
                Environment.MachineName,
                AppVersion,
                cts.Token);
            var current = result.Account;

            if (current.Role == UserRole.Student)
            {
                if (string.IsNullOrWhiteSpace(current.StudentCode))
                    throw new InvalidOperationException("Hồ sơ sinh viên chưa có mã sinh viên.");

                if (current.DateOfBirth is null)
                    throw new InvalidOperationException("Hồ sơ sinh viên chưa có ngày sinh.");
            }

            if (current.Role is not (UserRole.Student or UserRole.Teacher or UserRole.Admin))
                throw new InvalidOperationException(ErrorCodes.AuthenticatedRoleInvalid);

            authState.SetAuthenticated(
                current,
                result.AccessToken,
                result.Authority,
                result.RefreshToken);
            authState.SetTransientCredentials(Account, Password);
            Password = string.Empty;
            Status = "Đăng nhập thành công.";
            StatusTone = "success";
            await authenticated();
        }
        catch (Exception ex)
        {
            authState.Clear();
            api.SetAccountToken(null);
            api.SetParticipantToken(null);
            AppServices.PublicCloud.Logout();
            FrontendLogger.Log(ex, "LoginViewModel");
            Status = ex.Message;
            StatusTone = "danger";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommand() =>
        (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    private static string AppVersion => ReleaseIdentity.SemanticVersion;

    private static string EnsureDeviceId()
    {
        var stored = AppServices.Preferences.Get("device-id");
        if (!string.IsNullOrWhiteSpace(stored)) return stored;

        var generated = "ET-" + Guid.NewGuid().ToString("N");
        AppServices.Preferences.Set("device-id", generated);
        return generated;
    }
}
