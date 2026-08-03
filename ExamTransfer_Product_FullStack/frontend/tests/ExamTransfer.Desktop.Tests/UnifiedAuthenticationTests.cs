using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class UnifiedAuthenticationTests
{
    [Fact]
    public async Task LoginRoleTransitions_DoNotRetainThePreviousAccountShellRole()
    {
        var path = SessionPath("role-transition");
        try
        {
            var teacher = Account(UserRole.Teacher);
            var student = Account(UserRole.Student);
            var authentication = new SequencedAuthentication(
                new UnifiedLoginResult(
                    teacher,
                    "teacher-local-token",
                    AuthSessionAuthority.LocalServer),
                new UnifiedLoginResult(
                    student,
                    Jwt(Guid.Parse(student.ProviderUserId!), student.LoginSessionId),
                    AuthSessionAuthority.Supabase),
                new UnifiedLoginResult(
                    teacher,
                    "teacher-local-token-2",
                    AuthSessionAuthority.LocalServer));
            var backend = new BackendClient(
                "http://localhost:5048",
                new RejectingBackendHandler());
            var state = new AppAuthSessionState(path);
            var viewModel = new LoginViewModel(
                backend,
                state,
                () => Task.CompletedTask,
                authentication);

            await ExecuteLoginAsync(viewModel, authentication, 1);
            Assert.True(state.IsTeacher);
            Assert.False(state.IsStudent);

            state.Clear();
            await ExecuteLoginAsync(viewModel, authentication, 2);
            Assert.True(state.IsStudent);
            Assert.False(state.IsTeacher);

            state.Clear();
            await ExecuteLoginAsync(viewModel, authentication, 3);
            Assert.True(state.IsTeacher);
            Assert.False(state.IsStudent);
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public async Task FailedLogin_ClearsPreviousRoleProfileAndAllBackendTokens()
    {
        var path = SessionPath("failed-login");
        try
        {
            var backend = new BackendClient(
                "http://localhost:5048",
                new RejectingBackendHandler());
            backend.SetAccountToken("old-account-token");
            backend.SetParticipantToken("old-participant-token");
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                Account(UserRole.Teacher),
                "old-account-token",
                AuthSessionAuthority.LocalServer);
            var authentication = new SequencedAuthentication(
                new InvalidOperationException("INVALID_LOGIN"));
            var viewModel = new LoginViewModel(
                backend,
                state,
                () => Task.CompletedTask,
                authentication);

            await ExecuteLoginAsync(viewModel, authentication, 1);

            Assert.False(state.IsAuthenticated);
            Assert.Null(state.CurrentAccount);
            Assert.Null(state.AccountAccessToken);
            Assert.False(backend.HasTrustedAccountToken);
            Assert.Contains("INVALID_LOGIN", viewModel.Status, StringComparison.Ordinal);
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public void StudentSessionRestore_RequiresJwtSubjectAndCachedProfileToMatch()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "examtransfer-auth-cache-" + Guid.NewGuid().ToString("N"),
            "session.bin");
        try
        {
            var userId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var account = StudentAccount(userId, sessionId);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                account,
                Jwt(userId, sessionId),
                AuthSessionAuthority.Supabase);

            var restoredState = new AppAuthSessionState(path);
            Assert.True(restoredState.TryRestoreAuthenticatedSession(out var restored));
            Assert.Equal(userId, restored.Account.UserId);
            Assert.Equal(UserRole.Student, restored.Account.Role);
            Assert.Equal(AuthSessionAuthority.Supabase, restored.Authority);

            state.SetAuthenticated(
                account,
                Jwt(Guid.NewGuid(), sessionId),
                AuthSessionAuthority.Supabase);
            Assert.False(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out _));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StudentSessionRestore_UsesProtectedRefreshTokenAfterAccessExpiry()
    {
        var path = SessionPath("student-refresh-restore");
        try
        {
            var userId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var account = StudentAccount(userId, sessionId) with
            {
                ExpiresAtUtc = expiredAt
            };
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                account,
                Jwt(userId, sessionId, expiredAt),
                AuthSessionAuthority.Supabase,
                "refresh-token-redacted");

            Assert.True(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out var restored));
            Assert.Equal("refresh-token-redacted", restored.RefreshToken);

            state.SetAuthenticated(
                account,
                Jwt(userId, sessionId, expiredAt),
                AuthSessionAuthority.Supabase);
            Assert.False(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out _));
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public void StudentRefreshRotation_ReplacesProtectedTokensInTheSessionCache()
    {
        var path = SessionPath("student-refresh-rotation");
        try
        {
            var userId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var account = StudentAccount(userId, sessionId);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                account,
                Jwt(userId, sessionId),
                AuthSessionAuthority.Supabase,
                "old-refresh-redacted");
            var nextExpiry = DateTimeOffset.UtcNow.AddHours(2);
            var nextAccess = Jwt(userId, sessionId, nextExpiry);

            Assert.True(state.UpdateSupabaseSession(
                nextAccess,
                "new-refresh-redacted",
                nextExpiry));
            Assert.True(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out var restored));
            Assert.Equal(nextAccess, restored.AccessToken);
            Assert.Equal("new-refresh-redacted", restored.RefreshToken);
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public void LocalServerSessionRestore_RequiresSubjectRoleOrganizationAndExpiry()
    {
        var path = SessionPath("local-restore-binding");
        try
        {
            var account = Account(UserRole.Admin);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                account,
                LocalToken(account),
                AuthSessionAuthority.LocalServer);
            Assert.True(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out _));

            state.SetAuthenticated(
                account,
                LocalToken(account, userId: Guid.NewGuid()),
                AuthSessionAuthority.LocalServer);
            Assert.False(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out _));

            state.SetAuthenticated(
                account,
                LocalToken(account, organizationId: Guid.NewGuid().ToString("D")),
                AuthSessionAuthority.LocalServer);
            Assert.False(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out _));

            state.SetAuthenticated(
                account,
                LocalToken(
                    account,
                    expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                AuthSessionAuthority.LocalServer);
            Assert.False(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out _));
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public void LogoutClear_RemovesRoleProfileAndProtectedSession()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "examtransfer-auth-clear-" + Guid.NewGuid().ToString("N"),
            "session.bin");
        try
        {
            var userId = Guid.NewGuid();
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                StudentAccount(userId, Guid.NewGuid()),
                Jwt(userId, Guid.NewGuid()),
                AuthSessionAuthority.Supabase);

            state.Clear();

            Assert.False(state.IsAuthenticated);
            Assert.Null(state.CurrentAccount);
            Assert.Null(state.AccountAccessToken);
            Assert.False(File.Exists(path));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StudentLogin_UsesSupabaseProfileWithoutCallingOrStartingLocalhost()
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var supabaseHandler = new SupabaseAccountHandler(
            identity,
            organization,
            "Student");
        var backendHandler = new RejectingBackendHandler();
        var runtime = new RecordingRuntime();
        var service = Service(
            supabaseHandler,
            backendHandler,
            runtime,
            organization,
            AppContext.BaseDirectory);

        var result = await service.LoginAsync(
            "HS001",
            "correct-password",
            "device-1",
            "student-pc",
            "1.3.5",
            default);

        Assert.Equal(UserRole.Student, result.Account.Role);
        Assert.Equal(AuthSessionAuthority.Supabase, result.Authority);
        Assert.Equal(identity.ToString("D"), result.Account.ProviderUserId);
        Assert.Equal(0, backendHandler.RequestCount);
        Assert.Equal(0, runtime.StartCount);
        Assert.DoesNotContain(
            supabaseHandler.RequestUris,
            uri => uri.IsLoopback || uri.Port == 5048);
    }

    [Fact]
    public async Task ExpiredRestoredStudentSession_RefreshesAndReloadsAuthoritativeProfile()
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var handler = new SupabaseAccountHandler(
            identity,
            organization,
            "Student");
        var options = new FixedPublicCloudRuntimeOptionsProvider(
            new PublicCloudRuntimeOptions(
                new Uri("https://project.supabase.test"),
                "sb_publishable_test_key",
                null,
                "Test",
                organization));
        var cloud = new SupabasePublicCloudClient(
            new HttpClient(handler),
            optionsProvider: options);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        Assert.True(cloud.TryRestoreSession(
            Jwt(identity, sessionId, expiredAt),
            "stored-refresh-redacted",
            identity.ToString("D"),
            expiredAt));

        var restored = await cloud.RestoreAuthenticatedAccountAsync(
            "device-1",
            default);

        Assert.Equal(identity, restored.Account.UserId);
        Assert.Equal(UserRole.Student, restored.Account.Role);
        Assert.Equal("refresh-token-redacted", restored.RefreshToken);
        Assert.Contains(
            handler.RequestUris,
            uri => uri.AbsolutePath == "/auth/v1/token"
                && uri.Query.Contains("grant_type=refresh_token", StringComparison.Ordinal));
        Assert.Contains(
            handler.RequestUris,
            uri => uri.AbsolutePath == "/rest/v1/profiles");
    }

    [Fact]
    public async Task StudentLogout_RevokesSupabaseSessionBeforeClearingMemory()
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var handler = new SupabaseAccountHandler(
            identity,
            organization,
            "Student");
        var options = new FixedPublicCloudRuntimeOptionsProvider(
            new PublicCloudRuntimeOptions(
                new Uri("https://project.supabase.test"),
                "sb_publishable_test_key",
                null,
                "Test",
                organization));
        var cloud = new SupabasePublicCloudClient(
            new HttpClient(handler),
            optionsProvider: options);
        await cloud.AuthenticateAccountAsync(
            "HS001",
            "correct-password",
            "device-1",
            default);

        await cloud.LogoutAsync();

        Assert.False(cloud.Authenticated);
        Assert.Contains(
            handler.RequestUris,
            uri => uri.AbsolutePath == "/auth/v1/logout");
    }

    [Fact]
    public async Task TeacherProfileWithStudentCode_FailsClosedWithoutRoleOverride()
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var backendHandler = new RejectingBackendHandler();
        var runtime = new RecordingRuntime();
        var service = Service(
            new SupabaseAccountHandler(
                identity,
                organization,
                "Teacher",
                studentCode: "HS001"),
            backendHandler,
            runtime,
            organization,
            AppContext.BaseDirectory);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            service.LoginAsync(
                "teacher@example.test",
                "correct-password",
                "device-1",
                "teacher-pc",
                "1.3.7",
                default));

        Assert.Equal(ErrorCodes.AuthenticatedRoleInvalid, error.Code);
        Assert.Equal(0, backendHandler.RequestCount);
        Assert.Equal(0, runtime.StartCount);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("student_code")]
    [InlineData("date_of_birth")]
    [InlineData("username_mismatch")]
    public async Task InvalidStudentProfile_FailsBeforeAnyLocalServerAction(
        string invalidField)
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var supabaseHandler = new SupabaseAccountHandler(
            identity,
            organization,
            "Student",
            username: invalidField == "username_mismatch" ? "OTHER" : null,
            usernameMissing: invalidField == "username",
            studentCodeMissing: invalidField == "student_code",
            dateOfBirthMissing: invalidField == "date_of_birth");
        var backendHandler = new RejectingBackendHandler();
        var runtime = new RecordingRuntime();
        var service = Service(
            supabaseHandler,
            backendHandler,
            runtime,
            organization,
            AppContext.BaseDirectory);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            service.LoginAsync(
                "HS001",
                "correct-password",
                "device-1",
                "student-pc",
                "1.3.7",
                default));

        Assert.Equal(ErrorCodes.AuthenticatedRoleInvalid, error.Code);
        Assert.Equal(0, backendHandler.RequestCount);
        Assert.Equal(0, runtime.StartCount);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("organization")]
    [InlineData("subject")]
    [InlineData("profile_id")]
    [InlineData("missing_profile")]
    [InlineData("expired")]
    public async Task InvalidAuthenticatedProfile_FailsClosedBeforeServerStartup(
        string failure)
    {
        var identity = Guid.NewGuid();
        var configuredOrganization = Guid.NewGuid();
        var profileOrganization = failure == "organization"
            ? Guid.NewGuid()
            : configuredOrganization;
        var supabaseHandler = new SupabaseAccountHandler(
            identity,
            profileOrganization,
            "Admin",
            usernameMissing: true,
            isActive: failure != "inactive",
            jwtSubject: failure == "subject" ? Guid.NewGuid() : null,
            profileUserId: failure == "profile_id" ? Guid.NewGuid() : null,
            profileExists: failure != "missing_profile",
            jwtExpiresAt: failure == "expired"
                ? DateTimeOffset.UtcNow.AddMinutes(-1)
                : null);
        var backendHandler = new RejectingBackendHandler();
        var runtime = new RecordingRuntime();
        var service = Service(
            supabaseHandler,
            backendHandler,
            runtime,
            configuredOrganization,
            AppContext.BaseDirectory);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            service.LoginAsync(
                "admin@example.test",
                "correct-password",
                "device-1",
                "admin-pc",
                "1.3.7",
                default));

        Assert.Equal(ErrorCodes.AuthenticatedRoleInvalid, error.Code);
        Assert.Equal(0, backendHandler.RequestCount);
        Assert.Equal(0, runtime.StartCount);
    }

    [Theory]
    [InlineData("Teacher", UserRole.Teacher)]
    [InlineData("Admin", UserRole.Admin)]
    public async Task TeacherOrAdminLogin_StartsOneServerAndRequiresMatchingLocalProfile(
        string cloudRole,
        UserRole expectedRole)
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        using var layout = TestLayout.Create();
        var supabaseHandler = new SupabaseAccountHandler(
            identity,
            organization,
            cloudRole,
            usernameMissing: true);
        var backendHandler = new LocalAccountHandler(
            identity,
            organization,
            expectedRole);
        var runtime = new RecordingRuntime { BecomeHealthyAfterStart = true };
        var service = Service(
            supabaseHandler,
            backendHandler,
            runtime,
            organization,
            layout.Client);

        var result = await service.LoginAsync(
            "teacher@example.test",
            "correct-password",
            "device-1",
            "teacher-pc",
            "1.3.5",
            default);

        Assert.Equal(expectedRole, result.Account.Role);
        Assert.Equal(AuthSessionAuthority.LocalServer, result.Authority);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(
            ["/api/v1/auth/login", "/api/v1/auth/me"],
            backendHandler.RequestPaths);
    }

    [Fact]
    public async Task UnknownRole_FailsClosedBeforeAnyLocalServerRequest()
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var supabaseHandler = new SupabaseAccountHandler(
            identity,
            organization,
            "Owner");
        var backendHandler = new RejectingBackendHandler();
        var runtime = new RecordingRuntime();
        var service = Service(
            supabaseHandler,
            backendHandler,
            runtime,
            organization,
            AppContext.BaseDirectory);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            service.LoginAsync(
                "owner@example.test",
                "correct-password",
                "device-1",
                "unknown-role-pc",
                "1.3.5",
                default));

        Assert.Equal(ErrorCodes.AuthenticatedRoleInvalid, error.Code);
        Assert.Equal(0, backendHandler.RequestCount);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task LocalProfileMismatch_StopsTheServerStartedForLogin()
    {
        var identity = Guid.NewGuid();
        var organization = Guid.NewGuid();
        using var layout = TestLayout.Create();
        var runtime = new RecordingRuntime { BecomeHealthyAfterStart = true };
        var service = Service(
            new SupabaseAccountHandler(
                identity,
                organization,
                "Teacher",
                usernameMissing: true),
            new LocalAccountHandler(
                identity,
                organization,
                UserRole.Admin),
            runtime,
            organization,
            layout.Client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoginAsync(
                "teacher@example.test",
                "correct-password",
                "device-1",
                "teacher-pc",
                "1.3.7",
                default));

        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.OwnedStopCount);
        Assert.Equal(0, runtime.StopExactCount);
        Assert.False(runtime.Healthy);
    }

    private static UnifiedAuthenticationService Service(
        HttpMessageHandler supabaseHandler,
        HttpMessageHandler backendHandler,
        RecordingRuntime runtime,
        Guid organization,
        string baseDirectory)
    {
        var options = new FixedPublicCloudRuntimeOptionsProvider(
            new PublicCloudRuntimeOptions(
                new Uri("https://project.supabase.test"),
                "sb_publishable_test_key",
                null,
                "Test",
                organization));
        var cloud = new SupabasePublicCloudClient(
            new HttpClient(supabaseHandler),
            optionsProvider: options);
        var backend = new BackendClient(
            "http://localhost:5048",
            backendHandler);
        var lifecycle = new LocalServerLifecycleService(
            runtime,
            baseDirectory);
        return new(backend, cloud, lifecycle);
    }

    private static string Jwt(
        Guid subject,
        Guid sessionId,
        DateTimeOffset? expiresAt = null)
    {
        static string Encode(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        return $"{Encode(new { alg = "none" })}.{Encode(new
        {
            sub = subject,
            session_id = sessionId,
            exp = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(1))
                .ToUnixTimeSeconds()
        })}.signature";
    }

    private static CurrentAccountDto StudentAccount(
        Guid userId,
        Guid sessionId) =>
        new(
            userId,
            "HS001",
            "hs001@students.examtransfer.local",
            "Học sinh",
            "HS001",
            UserRole.Student,
            Guid.NewGuid().ToString("D"),
            sessionId,
            "device-1",
            DateTimeOffset.UtcNow.AddHours(1),
            new DateOnly(2010, 1, 1),
            false,
            userId.ToString("D"));

    private static string LocalToken(
        CurrentAccountDto account,
        Guid? userId = null,
        UserRole? role = null,
        string? organizationId = null,
        DateTimeOffset? expiresAt = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            userId = userId ?? account.UserId,
            loginSessionId = account.LoginSessionId,
            role = (int)(role ?? account.Role),
            organizationId = organizationId ?? account.OrganizationId,
            deviceId = account.DeviceId,
            exp = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(1))
                .ToUnixTimeSeconds()
        });
        var encoded = Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{encoded}.signature";
    }

    private static CurrentAccountDto Account(UserRole role)
    {
        var providerUserId = Guid.NewGuid();
        return new(
            Guid.NewGuid(),
            role == UserRole.Student ? "HS001" : "teacher",
            role == UserRole.Student
                ? "hs001@students.examtransfer.local"
                : "teacher@example.test",
            role == UserRole.Student ? "Há»c sinh" : "GiÃ¡o viÃªn",
            role == UserRole.Student ? "HS001" : null,
            role,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid(),
            "device-1",
            DateTimeOffset.UtcNow.AddHours(1),
            role == UserRole.Student ? new DateOnly(2010, 1, 1) : null,
            false,
            providerUserId.ToString("D"));
    }

    private static string SessionPath(string scope) =>
        Path.Combine(
            Path.GetTempPath(),
            $"examtransfer-auth-{scope}-{Guid.NewGuid():N}",
            "session.bin");

    private static void DeleteSessionDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static async Task ExecuteLoginAsync(
        LoginViewModel viewModel,
        SequencedAuthentication authentication,
        int expectedCalls)
    {
        viewModel.Account = "account";
        viewModel.Password = "password";
        viewModel.LoginCommand.Execute(null);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while ((authentication.Calls < expectedCalls || viewModel.IsBusy)
               && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(expectedCalls, authentication.Calls);
        Assert.False(viewModel.IsBusy);
    }

    private sealed class SupabaseAccountHandler(
        Guid userId,
        Guid organizationId,
        string role,
        string? username = null,
        string? studentCode = null,
        string? dateOfBirth = null,
        bool usernameMissing = false,
        bool studentCodeMissing = false,
        bool dateOfBirthMissing = false,
        bool isActive = true,
        Guid? jwtSubject = null,
        Guid? profileUserId = null,
        bool profileExists = true,
        DateTimeOffset? jwtExpiresAt = null) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
            {
                return Ok(new Dictionary<string, object?>
                {
                    ["access_token"] = Jwt(
                        jwtSubject ?? userId,
                        Guid.NewGuid(),
                        jwtExpiresAt),
                    ["refresh_token"] = "refresh-token-redacted",
                    ["expires_in"] = 3600,
                    ["user"] = new Dictionary<string, object?>
                    {
                        ["id"] = (profileUserId ?? userId).ToString("D"),
                        ["email"] = role == "Student"
                            ? "hs001@students.examtransfer.local"
                            : "teacher@example.test"
                    }
                });
            }

            if (request.RequestUri.AbsolutePath == "/rest/v1/profiles")
            {
                if (!profileExists)
                    return Ok(Array.Empty<object>());
                return Ok(new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = userId.ToString("D"),
                        ["organization_id"] = organizationId.ToString("D"),
                        ["username"] = usernameMissing
                            ? null
                            : role == "Student"
                                ? username ?? "HS001"
                                : username ?? "teacher",
                        ["display_name"] = role == "Student" ? "Học sinh" : "Giáo viên",
                        ["student_code"] = studentCodeMissing
                            ? null
                            : role == "Student" ? studentCode ?? "HS001" : studentCode,
                        ["date_of_birth"] = dateOfBirthMissing
                            ? null
                            : role == "Student" ? dateOfBirth ?? "2010-01-01" : dateOfBirth,
                        ["must_change_password"] = false,
                        ["role"] = role,
                        ["is_active"] = isActive
                    }
                });
            }

            if (request.RequestUri.AbsolutePath == "/auth/v1/logout")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok<T>(T body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            });
    }

    private sealed class RejectingBackendHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.InternalServerError));
        }
    }

    private sealed class SequencedAuthentication(
        params object[] outcomes) : IUnifiedAuthenticationService
    {
        private readonly Queue<object> remaining = new(outcomes);
        public int Calls { get; private set; }

        public Task<UnifiedLoginResult> LoginAsync(
            string account,
            string password,
            string deviceId,
            string machineName,
            string appVersion,
            CancellationToken cancellationToken)
        {
            Calls++;
            var outcome = remaining.Dequeue();
            return outcome switch
            {
                UnifiedLoginResult result => Task.FromResult(result),
                Exception error => Task.FromException<UnifiedLoginResult>(error),
                _ => throw new InvalidOperationException("Invalid authentication fixture.")
            };
        }
    }

    private sealed class LocalAccountHandler(
        Guid providerUserId,
        Guid organizationId,
        UserRole role) : HttpMessageHandler
    {
        private readonly CurrentAccountDto current = new(
            Guid.NewGuid(),
            role == UserRole.Admin ? "admin" : "teacher",
            "teacher@example.test",
            role == UserRole.Admin ? "Quản trị viên" : "Giáo viên",
            null,
            role,
            organizationId.ToString("D"),
            Guid.NewGuid(),
            "device-1",
            DateTimeOffset.UtcNow.AddHours(1),
            ProviderUserId: providerUserId.ToString("D"));

        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            object data = request.RequestUri.AbsolutePath switch
            {
                "/api/v1/auth/login" => new AccountLoginResultDto(
                    true,
                    false,
                    null,
                    current.UserId,
                    current.DisplayName,
                    null,
                    role,
                    current.OrganizationId,
                    "local-account-token",
                    current.ExpiresAtUtc,
                    current.DeviceId),
                "/api/v1/auth/me" => current,
                _ => throw new InvalidOperationException(
                    $"Unexpected backend request {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    ApiResponse<object>.Ok(data, "trace"))
            });
        }
    }

    private sealed class RecordingRuntime : ILocalServerRuntime
    {
        private bool healthy;
        public bool BecomeHealthyAfterStart { get; init; }
        public int StartCount { get; private set; }
        public int OwnedStopCount { get; private set; }
        public int StopExactCount { get; private set; }
        public bool Healthy => healthy;

        public Task<LocalServerProbeResult> ProbeAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(healthy
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
                : new LocalServerProbeResult("NOT_RUNNING"));

        public Task<bool> IsTcpPortOccupiedAsync(
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> IsUdpPortOccupiedAsync(
            CancellationToken cancellationToken) => Task.FromResult(false);

        public ILocalServerProcess Start(
            string executablePath,
            string workingDirectory)
        {
            StartCount++;
            healthy = BecomeHealthyAfterStart;
            return new Process(() =>
            {
                OwnedStopCount++;
                healthy = false;
            });
        }

        public Task StopExactAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopExactCount++;
            healthy = false;
            return Task.CompletedTask;
        }

        private sealed class Process(Action onStop) : ILocalServerProcess
        {
            public bool HasExited { get; private set; }
            public int? ExitCode => HasExited ? 0 : null;
            public Task StopAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasExited)
                    return Task.CompletedTask;
                HasExited = true;
                onStop();
                return Task.CompletedTask;
            }
        }
    }

    private sealed record TestLayout(
        string Root,
        string Client) : IDisposable
    {
        public static TestLayout Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "examtransfer-unified-auth-" + Guid.NewGuid().ToString("N"));
            var client = Path.Combine(root, "Client");
            var server = Path.Combine(root, "Server");
            Directory.CreateDirectory(client);
            Directory.CreateDirectory(server);
            File.WriteAllBytes(
                Path.Combine(server, "ExamTransfer.LocalServer.exe"),
                Encoding.UTF8.GetBytes("fixture"));
            return new(root, client);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
