using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Cloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class SupabaseIdentityLoginTests
{
    private const string OrganizationId = "516543f3-ca00-480e-87ca-683243ffdc0b";
    private const string OtherOrganizationId = "ad085648-b954-4b0f-9ad6-fd7b8a727fd0";
    private const string ProviderUserId = "11f88943-ab77-4052-b4f1-83c13fb5dc93";
    private static readonly string UserAccessToken = Jwt(ProviderUserId);

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Teacher)]
    public async Task ValidProfile_ProvisionsAccountAndIssuesUsableApplicationToken(UserRole role)
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(
            Profile(role, usernameMissing: true));
        var harness = CreateHarness(database.Context, handler);

        var result = await harness.Auth.LoginAsync(LoginRequest(), "127.0.0.1", CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.Equal(role, result.Role);
        Assert.NotNull(result.AccessToken);
        var principal = harness.Tokens.ValidateAccountToken(result.AccessToken!);
        Assert.NotNull(principal);
        Assert.NotNull(await harness.Sessions.ValidateAsync(principal!, CancellationToken.None));

        var user = await database.Context.UsersSet.SingleAsync();
        Assert.Equal(ProviderUserId, user.SupabaseAuthUserId);
        Assert.Equal(OrganizationId, user.OrganizationId);
        Assert.Equal(role, user.Role);
        Assert.Equal(1, handler.PasswordGrantCalls);
        Assert.Equal(1, handler.ProfileCalls);
        Assert.All(handler.ProfileAuthorization, header =>
        {
            Assert.Equal("Bearer", header.Scheme);
            Assert.Equal(UserAccessToken, header.Parameter);
        });
        Assert.All(handler.ProfileApiKeys, key => Assert.Equal("test-publishable-key", key));

        var session = await database.Context.UserLoginSessionsSet.SingleAsync();
        Assert.NotNull(session.EncryptedRefreshToken);
        Assert.NotEqual("test-refresh-token", session.EncryptedRefreshToken);
    }

    [Fact]
    public async Task StudentCodeLogin_ProvisionsStudentAndIssuesTokenDirectly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Student));
        var harness = CreateHarness(database.Context, handler);

        var result = await harness.Auth.LoginAsync(StudentLoginRequest(), null, CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.False(result.RequiresStudentConfirmation);
        Assert.Null(result.ChallengeToken);
        Assert.NotNull(result.AccessToken);
        var user = await database.Context.UsersSet.SingleAsync();
        Assert.Equal(UserRole.Student, user.Role);
        Assert.Equal("23174800110", user.StudentCode);
        Assert.Equal(new DateOnly(2005, 5, 9), user.DateOfBirth);
        Assert.True(user.MustChangePassword);
        Assert.Contains("23174800110@students.examtransfer.local", handler.PasswordGrantBodies.Single());
        Assert.Equal(1, handler.PasswordGrantCalls);
    }

    [Theory]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(false, false, false, "DIFFERENT")]
    public async Task IncompleteOrInconsistentStudentProfile_FailsClosed(
        bool usernameMissing,
        bool studentCodeMissing,
        bool dateOfBirthMissing,
        string? studentUsername)
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(
            UserRole.Student,
            usernameMissing: usernameMissing,
            studentCodeMissing: studentCodeMissing,
            dateOfBirthMissing: dateOfBirthMissing,
            studentUsername: studentUsername));
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(
                StudentLoginRequest(),
                null,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.ProfileResponseInvalid, error.Code);
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(0, await database.Context.UsersSet.CountAsync());
    }

    [Fact]
    public async Task StudentPasswordChange_ReauthenticatesUpdatesAuthAndCompletesProfileFlag()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Student))
        {
            AuthBody = JsonSerializer.Serialize(new
            {
                access_token = UserAccessToken,
                refresh_token = "test-refresh-token",
                expires_in = 3600,
                user = new
                {
                    id = ProviderUserId,
                    email = "23174800110@students.examtransfer.local"
                }
            })
        };
        var harness = CreateHarness(database.Context, handler);
        var login = await harness.Auth.LoginAsync(StudentLoginRequest(), null, CancellationToken.None);
        var principal = harness.Tokens.ValidateAccountToken(login.AccessToken!);
        var before = await AuthenticateAsync(harness, login.AccessToken!);
        Assert.Equal("true", before.Principal!.FindFirst("password_change_required")?.Value);

        var result = await harness.Auth.ChangePasswordAsync(
            principal!,
            new ChangePasswordRequest("correct-password", "Safe@2026X", "Safe@2026X"),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(2, handler.PasswordGrantCalls);
        Assert.Equal(1, handler.PasswordUpdateCalls);
        Assert.Equal(1, handler.PasswordProfileCalls);
        Assert.Contains("Safe@2026X", handler.PasswordUpdateBodies.Single());
        Assert.All(handler.PasswordUpdateAuthorization, header =>
        {
            Assert.Equal("Bearer", header.Scheme);
            Assert.Equal(UserAccessToken, header.Parameter);
        });
        var after = await AuthenticateAsync(harness, login.AccessToken!);
        Assert.Equal("false", after.Principal!.FindFirst("password_change_required")?.Value);
    }

    [Fact]
    public async Task SameDeviceLogin_IsIdempotentAndInvokesOnePasswordGrantPerOperation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Admin));
        var harness = CreateHarness(database.Context, handler);

        var first = await harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None);
        var second = await harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None);

        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(1, await database.Context.UsersSet.CountAsync());
        Assert.Equal(1, await database.Context.UserLoginSessionsSet.CountAsync());
        Assert.Equal(2, handler.PasswordGrantCalls);
        Assert.Equal(2, handler.ProfileCalls);
        var firstPrincipal = harness.Tokens.ValidateAccountToken(first.AccessToken!);
        var secondPrincipal = harness.Tokens.ValidateAccountToken(second.AccessToken!);
        Assert.Equal(firstPrincipal!.LoginSessionId, secondPrincipal!.LoginSessionId);
    }

    [Fact]
    public async Task ApplicationToken_RoundTripsThroughAccountAuthHandler()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Admin));
        var harness = CreateHarness(database.Context, handler);
        var login = await harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None);

        var authenticated = await AuthenticateAsync(harness, login.AccessToken!);

        Assert.True(authenticated.Succeeded);
        Assert.Equal(login.UserId.ToString(), authenticated.Principal!.FindFirst("sub")?.Value);
        Assert.Equal(ProviderUserId, authenticated.Principal.FindFirst("provider_user_id")?.Value);
        Assert.Equal(OrganizationId, authenticated.Principal.FindFirst("organization_id")?.Value);
        Assert.Equal(UserRole.Admin.ToString(), authenticated.Principal.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value);
        var tokenPrincipal = harness.Tokens.ValidateAccountToken(login.AccessToken!);
        Assert.Equal(tokenPrincipal!.LoginSessionId.ToString(), authenticated.Principal.FindFirst("login_session_id")?.Value);

        var replacement = login.AccessToken![^1] == 'A' ? 'B' : 'A';
        var tampered = login.AccessToken[..^1] + replacement;
        Assert.False((await AuthenticateAsync(harness, tampered)).Succeeded);

        var expired = harness.Tokens.IssueAccountToken(
            login.UserId!.Value,
            tokenPrincipal.LoginSessionId,
            UserRole.Admin,
            OrganizationId,
            "device-1",
            TimeSpan.FromSeconds(-1));
        Assert.False((await AuthenticateAsync(harness, expired.Token)).Succeeded);
    }

    [Fact]
    public async Task MissingProfile_IsReportedDistinctlyAndDoesNotCreateLocalUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler("[]");
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ProfileNotFound, error.Code);
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(0, await database.Context.UsersSet.CountAsync());
        Assert.Equal(1, handler.PasswordGrantCalls);
    }

    [Fact]
    public async Task OrganizationMismatch_IsReportedDistinctly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Admin, OtherOrganizationId));
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ProfileOrganizationMismatch, error.Code);
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task InvalidRole_IsReportedDistinctly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile("Owner"));
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ProfileRoleInvalid, error.Code);
        Assert.Equal(403, error.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCodes.ProfileAccessUnauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, ErrorCodes.ProfileAccessForbidden, 403)]
    public async Task ProfileAuthorizationFailures_AreReportedDistinctly(
        HttpStatusCode status,
        string expectedCode,
        int expectedStatus)
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Admin)) { ProfileStatus = status };
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedStatus, error.StatusCode);
        Assert.Equal(1, handler.PasswordGrantCalls);
    }

    [Theory]
    [InlineData(true, ErrorCodes.AuthResponseInvalid)]
    [InlineData(false, ErrorCodes.ProfileResponseInvalid)]
    public async Task MalformedJson_IsReportedForTheCorrectStage(bool malformedAuth, string expectedCode)
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(malformedAuth ? Profile(UserRole.Admin) : "{not-json");
        if (malformedAuth)
            handler.AuthBody = "{not-json";
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, await database.Context.UsersSet.CountAsync());
    }

    [Fact]
    public async Task MissingSupabaseAccessToken_IsReportedDistinctly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Admin))
        {
            AuthBody = JsonSerializer.Serialize(new
            {
                refresh_token = "test-refresh-token",
                expires_in = 3600,
                user = new { id = ProviderUserId, email = "admin@example.test" }
            })
        };
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(ErrorCodes.AuthAccessTokenMissing, error.Code);
        Assert.Equal(0, handler.ProfileCalls);
        Assert.Equal(0, await database.Context.UsersSet.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupabaseJwtSubjectMismatchOrExpiry_FailsBeforeProfileLookup(
        bool expired)
    {
        await using var database = await TestDatabase.CreateAsync();
        var subject = expired ? ProviderUserId : Guid.NewGuid().ToString("D");
        var handler = new SupabaseHandler(Profile(UserRole.Admin))
        {
            AuthBody = JsonSerializer.Serialize(new
            {
                access_token = Jwt(
                    subject,
                    expired
                        ? DateTimeOffset.UtcNow.AddMinutes(-1)
                        : DateTimeOffset.UtcNow.AddHours(1)),
                refresh_token = "test-refresh-token",
                expires_in = 3600,
                user = new
                {
                    id = ProviderUserId,
                    email = "admin@example.test"
                }
            })
        };
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(
                LoginRequest(),
                null,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.AuthResponseInvalid, error.Code);
        Assert.Equal(0, handler.ProfileCalls);
        Assert.Equal(0, await database.Context.UsersSet.CountAsync());
    }

    [Fact]
    public async Task InactiveSupabaseProfile_IsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new SupabaseHandler(Profile(UserRole.Teacher, isActive: false));
        var harness = CreateHarness(database.Context, handler);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.LoginAsync(LoginRequest(), null, CancellationToken.None));

        Assert.Equal(ErrorCodes.AccountInactive, error.Code);
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task TeacherLogin_HandoffsEncryptedCloudSessionSignalsWorkerAndLogoutStopsIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExamTransfer.Phase3.Auth",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var database = await TestDatabase.CreateAsync();
            var handler = new SupabaseHandler(Profile(UserRole.Teacher));
            var paths = new TestStoragePaths(root);
            var protection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")));
            var cloudSession = new CloudSessionState(protection, paths);
            var signal = new CloudSyncSignal();
            var harness = CreateHarness(
                database.Context,
                handler,
                protection,
                cloudSession,
                signal);

            var result = await harness.Auth.LoginAsync(
                LoginRequest(),
                "127.0.0.1",
                CancellationToken.None);

            var snapshot = Assert.IsType<CloudSessionSnapshot>(
                cloudSession.Snapshot);
            Assert.Equal(UserAccessToken, snapshot.AccessToken);
            Assert.Equal("test-refresh-token", snapshot.RefreshToken);
            Assert.Equal(ProviderUserId, snapshot.UserId);
            Assert.Equal(OrganizationId, snapshot.OrganizationId);
            Assert.Equal(UserRole.Teacher.ToString(), snapshot.Role);
            Assert.True(await signal.WaitAsync(
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None));

            var protectedPath = Path.Combine(
                root,
                "config",
                "cloud-session.protected");
            var protectedPayload = await File.ReadAllTextAsync(protectedPath);
            Assert.DoesNotContain(UserAccessToken, protectedPayload, StringComparison.Ordinal);
            Assert.DoesNotContain("test-refresh-token", protectedPayload, StringComparison.Ordinal);

            var principal = harness.Tokens.ValidateAccountToken(result.AccessToken!);
            Assert.NotNull(principal);
            await harness.Auth.LogoutAsync(
                principal!,
                new LogoutRequest("device-1", "phase3-test"),
                CancellationToken.None);

            Assert.Null(cloudSession.Snapshot);
            Assert.False(File.Exists(protectedPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ExpiringCloudSession_RefreshesTokenAndRemainsSynchronizable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExamTransfer.Phase3.Refresh",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new SupabaseHandler(Profile(UserRole.Teacher))
            {
                AuthBody = JsonSerializer.Serialize(new
                {
                    access_token = "refreshed-access-token",
                    refresh_token = "refreshed-refresh-token",
                    expires_in = 3600,
                    user = new
                    {
                        id = ProviderUserId,
                        email = "admin@example.test"
                    }
                })
            };
            var paths = new TestStoragePaths(root);
            var protection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "keys")));
            var state = new CloudSessionState(protection, paths);
            state.Save(new CloudSessionSnapshot(
                "expiring-access-token",
                "expiring-refresh-token",
                DateTimeOffset.UtcNow.AddSeconds(-1),
                ProviderUserId,
                "admin@example.test",
                OrganizationId,
                UserRole.Teacher.ToString()));
            var options = TestOptions();
            var adapter = new SupabaseCloudAdapter(
                new HttpClient(handler),
                options,
                state);

            var result = await adapter.RefreshSessionAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("refreshed-access-token", result!.AccessToken);
            Assert.Equal("refreshed-refresh-token", result.RefreshToken);
            Assert.Equal("refreshed-access-token", state.Snapshot!.AccessToken);
            Assert.True(adapter.Authenticated);
            Assert.True(adapter.CanSynchronize);
            Assert.Equal(1, handler.PasswordGrantCalls);
            Assert.Equal(1, handler.ProfileCalls);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static AuthHarness CreateHarness(
        AppDbContext db,
        SupabaseHandler handler,
        IDataProtectionProvider? dataProtection = null,
        CloudSessionState? cloudSession = null,
        ICloudSyncSignal? cloudSyncSignal = null)
    {
        var options = TestOptions();
        var provider = new SupabaseIdentityClient(
            new HttpClient(handler),
            options,
            NullLogger<SupabaseIdentityClient>.Instance);
        var sessions = new AccountSessionService(db, options);
        var tokens = new AccountTokenService(options);
        var challenges = new LoginChallengeService(new MemoryCache(new MemoryCacheOptions()));
        dataProtection ??= new EphemeralDataProtectionProvider();
        var auth = new AccountAuthenticationService(
            db,
            provider,
            sessions,
            tokens,
            challenges,
            dataProtection,
            options,
            cloudSession,
            cloudSyncSignal);
        return new AuthHarness(auth, tokens, sessions, options);
    }

    private static IOptions<ExamTransferOptions> TestOptions() =>
        Options.Create(new ExamTransferOptions
        {
            Security = new SecurityOptions { TokenSigningKey = "supabase-login-test-signing-key" },
            Auth = new AuthOptions
            {
                AccountTokenMinutes = 30,
                HeartbeatSeconds = 30,
                LeaseSeconds = 120,
                ChallengeMinutes = 5
            },
            Cloud = new CloudOptions
            {
                Enabled = true,
                SupabaseUrl = "https://example.supabase.co",
                PublishableKey = "test-publishable-key",
                OrganizationId = OrganizationId,
                Schema = "public"
            }
        });

    private static async Task<AuthenticateResult> AuthenticateAsync(AuthHarness harness, string token)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccountTokenService>(harness.Tokens);
        services.AddSingleton<IAccountSessionService>(harness.Sessions);
        services.AddSingleton<IOptions<ExamTransferOptions>>(harness.Options);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddAuthentication(ExamTransferAuthSchemes.Account)
            .AddScheme<AuthenticationSchemeOptions, AccountAuthHandler>(ExamTransferAuthSchemes.Account, _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return await context.AuthenticateAsync(ExamTransferAuthSchemes.Account);
    }

    private static AccountLoginRequest LoginRequest() =>
        new("admin@example.test", "correct-password", "device-1", "machine-1", "test");

    private static AccountLoginRequest StudentLoginRequest() =>
        new("23174800110", "correct-password", "device-1", "machine-1", "test");

    private static string Profile(
        UserRole role,
        string organizationId = OrganizationId,
        bool isActive = true,
        bool usernameMissing = false,
        bool studentCodeMissing = false,
        bool dateOfBirthMissing = false,
        string? studentUsername = null) =>
        Profile(
            role.ToString(),
            organizationId,
            isActive,
            usernameMissing,
            studentCodeMissing,
            dateOfBirthMissing,
            studentUsername);

    private static string Profile(
        string role,
        string organizationId = OrganizationId,
        bool isActive = true,
        bool usernameMissing = false,
        bool studentCodeMissing = false,
        bool dateOfBirthMissing = false,
        string? studentUsername = null) =>
        JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = ProviderUserId,
                ["organization_id"] = organizationId,
                ["username"] = usernameMissing
                    ? null
                    : role == UserRole.Student.ToString()
                        ? studentUsername ?? "23174800110"
                        : "admin",
                ["display_name"] = role == UserRole.Student.ToString()
                    ? "Nguyen Tuan Anh"
                    : "ExamTransfer Admin",
                ["student_code"] = studentCodeMissing
                    ? null
                    : role == UserRole.Student.ToString() ? "23174800110" : null,
                ["date_of_birth"] = dateOfBirthMissing
                    ? null
                    : role == UserRole.Student.ToString() ? "2005-05-09" : null,
                ["must_change_password"] = role == UserRole.Student.ToString(),
                ["role"] = role,
                ["is_active"] = isActive
            }
        });

    private static string Jwt(
        string subject,
        DateTimeOffset? expiresAt = null)
    {
        static string Encode(object value) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{Encode(new { alg = "none" })}.{Encode(new
        {
            sub = subject,
            exp = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(1))
                .ToUnixTimeSeconds()
        })}.signature";
    }

    private sealed record AuthHarness(
        AccountAuthenticationService Auth,
        AccountTokenService Tokens,
        AccountSessionService Sessions,
        IOptions<ExamTransferOptions> Options);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ExamTransfer.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) =>
            Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) =>
            Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) =>
            Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) =>
            Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }

    private sealed class SupabaseHandler(string profileBody) : HttpMessageHandler
    {
        public string AuthBody { get; set; } = JsonSerializer.Serialize(new
        {
            access_token = UserAccessToken,
            refresh_token = "test-refresh-token",
            expires_in = 3600,
            user = new { id = ProviderUserId, email = "admin@example.test" }
        });

        public HttpStatusCode ProfileStatus { get; set; } = HttpStatusCode.OK;
        public int PasswordGrantCalls { get; private set; }
        public int ProfileCalls { get; private set; }
        public int PasswordUpdateCalls { get; private set; }
        public int PasswordProfileCalls { get; private set; }
        public List<string> PasswordGrantBodies { get; } = [];
        public List<string> PasswordUpdateBodies { get; } = [];
        public List<AuthenticationHeaderValue> PasswordUpdateAuthorization { get; } = [];
        public List<AuthenticationHeaderValue> ProfileAuthorization { get; } = [];
        public List<string> ProfileApiKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/auth/v1/token", StringComparison.Ordinal) == true)
            {
                PasswordGrantCalls++;
                PasswordGrantBodies.Add(request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
                return JsonResponse(HttpStatusCode.OK, AuthBody);
            }

            if (request.Method == HttpMethod.Put
                && request.RequestUri?.AbsolutePath.EndsWith("/auth/v1/user", StringComparison.Ordinal) == true)
            {
                PasswordUpdateCalls++;
                PasswordUpdateBodies.Add(request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
                if (request.Headers.Authorization is not null)
                    PasswordUpdateAuthorization.Add(request.Headers.Authorization);
                return JsonResponse(HttpStatusCode.OK, "{}");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/rest/v1/rpc/complete_own_password_change",
                    StringComparison.Ordinal) == true)
            {
                PasswordProfileCalls++;
                return JsonResponse(HttpStatusCode.OK, "true");
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/rest/v1/profiles", StringComparison.Ordinal) == true)
            {
                ProfileCalls++;
                if (request.Headers.Authorization is not null)
                    ProfileAuthorization.Add(request.Headers.Authorization);
                if (request.Headers.TryGetValues("apikey", out var values))
                    ProfileApiKeys.Add(values.Single());
                return JsonResponse(ProfileStatus, profileBody);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri?.AbsolutePath}");
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public AppDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
