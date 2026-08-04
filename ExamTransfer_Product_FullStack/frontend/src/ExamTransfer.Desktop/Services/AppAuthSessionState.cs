using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public enum AuthSessionAuthority
{
    LocalServer,
    Supabase
}

public sealed record RestoredAuthSession(
    CurrentAccountDto Account,
    string AccessToken,
    AuthSessionAuthority Authority,
    string? RefreshToken);

public sealed class AppAuthSessionState : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private CurrentAccountDto? currentAccount;
    private string? accountAccessToken;
    private string? transientAccount;
    private byte[]? protectedTransientPassword;
    private readonly string storePath;

    public AppAuthSessionState(string? storePath = null)
    {
        this.storePath = storePath
            ?? Path.Combine(AppProfile.LocalDataRoot, "auth-session.bin");
    }

    public CurrentAccountDto? CurrentAccount
    {
        get => currentAccount;
        private set
        {
            if (!Set(ref currentAccount, value)) return;

            Raise(nameof(IsAuthenticated));
            Raise(nameof(IsTeacher));
            Raise(nameof(IsStudent));
            Raise(nameof(DisplayName));
            Raise(nameof(StudentCode));
            Raise(nameof(DateOfBirthText));
            Raise(nameof(RoleLabel));
            Raise(nameof(MustChangePassword));
        }
    }

    public string? AccountAccessToken
    {
        get => accountAccessToken;
        private set => Set(ref accountAccessToken, value);
    }

    public bool IsAuthenticated =>
        CurrentAccount is not null && !string.IsNullOrWhiteSpace(AccountAccessToken);

    public bool IsTeacher =>
        CurrentAccount?.Role is UserRole.Teacher or UserRole.Admin;

    public bool IsStudent =>
        CurrentAccount?.Role == UserRole.Student;

    public string DisplayName =>
        CurrentAccount?.DisplayName ?? "Chưa đăng nhập";

    public string StudentCode =>
        CurrentAccount?.StudentCode ?? string.Empty;

    public string DateOfBirthText =>
        CurrentAccount?.DateOfBirth?.ToString("dd/MM/yyyy") ?? "Chưa cập nhật";

    public bool MustChangePassword =>
        CurrentAccount?.MustChangePassword == true;

    public string RoleLabel => CurrentAccount?.Role switch
    {
        UserRole.Admin => "Quản trị viên",
        UserRole.Teacher => "Giáo viên",
        UserRole.Student => "Học sinh",
        _ => "Khách"
    };

    public string? TryRestoreAccessToken() =>
        TryRestoreAuthenticatedSession(out var session)
            ? session.AccessToken
            : null;

    public bool TryRestoreAuthenticatedSession(out RestoredAuthSession session)
    {
        session = default!;
        try
        {
            if (!File.Exists(storePath)) return false;

            var protectedBytes = File.ReadAllBytes(storePath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredAuthSession>(bytes, Json);
            if (stored?.Account is null
                || string.IsNullOrWhiteSpace(stored.AccessToken)
                || !ValidAuthorityBinding(stored))
            {
                Clear();
                return false;
            }

            session = new(
                stored.Account,
                stored.AccessToken,
                stored.Authority,
                stored.RefreshToken);
            return true;
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.Restore");
            Clear();
            return false;
        }
    }

    public void SetAuthenticated(
        CurrentAccountDto account,
        string accessToken,
        AuthSessionAuthority? authority = null,
        string? refreshToken = null)
    {
        if (account.Role is not (UserRole.Admin or UserRole.Teacher or UserRole.Student))
            throw new InvalidOperationException(ErrorCodes.AuthenticatedRoleInvalid);
        var effectiveAuthority = authority
            ?? (account.Role == UserRole.Student
                ? AuthSessionAuthority.Supabase
                : AuthSessionAuthority.LocalServer);
        if (effectiveAuthority == AuthSessionAuthority.Supabase
            && account.Role != UserRole.Student)
            throw new InvalidOperationException("Only Student sessions remain client-side Supabase sessions.");
        if (effectiveAuthority == AuthSessionAuthority.LocalServer
            && account.Role == UserRole.Student)
            throw new InvalidOperationException("Student Local Server sessions must not replace the authoritative Supabase login.");
        if (effectiveAuthority == AuthSessionAuthority.LocalServer)
            refreshToken = null;

        CurrentAccount = account;
        AccountAccessToken = accessToken;
        Save(account, accessToken, effectiveAuthority, refreshToken);
    }

    public bool UpdateSupabaseSession(
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAtUtc)
    {
        if (CurrentAccount is not { Role: UserRole.Student } account)
            return false;

        var updated = account with { ExpiresAtUtc = expiresAtUtc };
        var stored = new StoredAuthSession(
            accessToken,
            updated,
            AuthSessionAuthority.Supabase,
            refreshToken);
        if (!ValidAuthorityBinding(stored))
            return false;

        CurrentAccount = updated;
        AccountAccessToken = accessToken;
        Save(updated, accessToken, AuthSessionAuthority.Supabase, refreshToken);
        return true;
    }

    public void SetTransientCredentials(string account, string password)
    {
        ClearTransientCredentials();
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            return;

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            transientAccount = account.Trim();
            protectedTransientPassword = ProtectedData.Protect(
                passwordBytes,
                null,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public bool TryGetTransientCredentials(out string account, out string password)
    {
        account = string.Empty;
        password = string.Empty;
        if (string.IsNullOrWhiteSpace(transientAccount)
            || protectedTransientPassword is null)
            return false;

        byte[]? clearBytes = null;
        try
        {
            clearBytes = ProtectedData.Unprotect(
                protectedTransientPassword,
                null,
                DataProtectionScope.CurrentUser);
            account = transientAccount;
            password = Encoding.UTF8.GetString(clearBytes);
            return password.Length > 0;
        }
        catch (CryptographicException ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.TransientCredentials");
            ClearTransientCredentials();
            return false;
        }
        finally
        {
            if (clearBytes is not null)
                CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public void ClearTransientCredentials()
    {
        transientAccount = null;
        if (protectedTransientPassword is not null)
            CryptographicOperations.ZeroMemory(protectedTransientPassword);
        protectedTransientPassword = null;
    }

    public void Clear()
    {
        CurrentAccount = null;
        AccountAccessToken = null;
        ClearTransientCredentials();

        try
        {
            if (File.Exists(storePath))
                File.Delete(storePath);
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.Clear");
        }
    }

    private void Save(
        CurrentAccountDto account,
        string accessToken,
        AuthSessionAuthority authority,
        string? refreshToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new StoredAuthSession(
                    accessToken,
                    account,
                    authority,
                    refreshToken),
                Json);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(storePath, protectedBytes);
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.Save");
        }
    }

    private static bool ValidAuthorityBinding(StoredAuthSession stored)
    {
        if (stored.Authority == AuthSessionAuthority.LocalServer)
            return stored.Account!.ExpiresAtUtc > DateTimeOffset.UtcNow
                && stored.Account.Role is UserRole.Admin or UserRole.Teacher or UserRole.Student
                && ValidLocalServerTokenBinding(
                    stored.AccessToken,
                    stored.Account);
        if (stored.Authority != AuthSessionAuthority.Supabase
            || stored.Account!.Role != UserRole.Student
            || string.IsNullOrWhiteSpace(stored.Account.ProviderUserId)
            || !Guid.TryParse(stored.Account.OrganizationId, out var organizationId)
            || organizationId == Guid.Empty
            || !Guid.TryParse(stored.Account.ProviderUserId, out var providerId)
            || providerId != stored.Account.UserId)
            return false;

        try
        {
            var segments = stored.AccessToken.Split('.');
            if (segments.Length != 3) return false;
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var subject = document.RootElement.TryGetProperty("sub", out var sub)
                ? sub.GetString()
                : null;
            var expiresAt = document.RootElement.TryGetProperty("exp", out var exp)
                && exp.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : default;
            var sessionMatches = stored.Account.LoginSessionId == Guid.Empty
                || (document.RootElement.TryGetProperty("session_id", out var session)
                    && Guid.TryParse(session.GetString(), out var sessionId)
                    && sessionId == stored.Account.LoginSessionId);
            return string.Equals(
                    subject,
                    stored.Account.ProviderUserId,
                    StringComparison.OrdinalIgnoreCase)
                && sessionMatches
                && ((expiresAt > DateTimeOffset.UtcNow
                        && stored.Account.ExpiresAtUtc > DateTimeOffset.UtcNow)
                    || !string.IsNullOrWhiteSpace(stored.RefreshToken));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool ValidLocalServerTokenBinding(
        string token,
        CurrentAccountDto account)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 2) return false;
            var payload = segments[0].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(
                payload.Length + ((4 - payload.Length % 4) % 4),
                '=');
            using var document = JsonDocument.Parse(
                Convert.FromBase64String(payload));
            var root = document.RootElement;
            return root.TryGetProperty("userId", out var user)
                && Guid.TryParse(user.GetString(), out var userId)
                && userId == account.UserId
                && root.TryGetProperty("loginSessionId", out var session)
                && Guid.TryParse(session.GetString(), out var sessionId)
                && sessionId == account.LoginSessionId
                && root.TryGetProperty("role", out var role)
                && role.TryGetInt32(out var roleValue)
                && roleValue == (int)account.Role
                && root.TryGetProperty("organizationId", out var organization)
                && string.Equals(
                    organization.GetString(),
                    account.OrganizationId,
                    StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("exp", out var exp)
                && exp.TryGetInt64(out var unix)
                && DateTimeOffset.FromUnixTimeSeconds(unix) > DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (
            ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed record StoredAuthSession(
        string AccessToken,
        CurrentAccountDto? Account = null,
        AuthSessionAuthority Authority = AuthSessionAuthority.LocalServer,
        string? RefreshToken = null);
}
