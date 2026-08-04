using System.Security.Claims;
using System.Text.Encodings.Web;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Auth;

public static class ExamTransferAuthSchemes
{
    public const string Account = "ExamTransferAccount";
    public const string ExamParticipant = "ExamTransferParticipant";
}

public static class StudentParticipantScope
{
    public static bool IsValid(ClaimsPrincipal principal)
    {
        var account = principal.Identities.FirstOrDefault(x =>
            x.IsAuthenticated && x.AuthenticationType == ExamTransferAuthSchemes.Account);
        var participant = principal.Identities.FirstOrDefault(x =>
            x.IsAuthenticated && x.AuthenticationType == ExamTransferAuthSchemes.ExamParticipant);
        if (account is null || participant is null) return false;
        if (!account.HasClaim(ClaimTypes.Role, UserRole.Student.ToString())
            || !account.HasClaim("password_change_required", "false")) return false;
        if (!Guid.TryParse(account.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var accountUserId)
            || !Guid.TryParse(participant.FindFirst("user_id")?.Value, out var participantUserId)
            || accountUserId == Guid.Empty
            || accountUserId != participantUserId) return false;
        return Guid.TryParse(participant.FindFirst("session_id")?.Value, out _)
            && Guid.TryParse(participant.FindFirst("participant_id")?.Value, out _);
    }
}

public sealed class AccountAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccountTokenService tokenService,
    IAccountSessionService sessionService,
    IOptions<ExamTransferOptions> appOptions,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadBearerToken();
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        if (DevelopmentTokenAllowed(token))
        {
            return Success(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString()),
                new Claim(ClaimTypes.Name, "Development Teacher"),
                new Claim(ClaimTypes.Role, UserRole.Teacher.ToString()),
                new Claim("sub", Guid.Empty.ToString()),
                new Claim("login_session_id", Guid.Empty.ToString()),
                new Claim("device_id", Environment.MachineName)
            ]);
        }

        var principal = tokenService.ValidateAccountToken(token);
        if (principal is null)
            return AuthenticateResult.Fail("Invalid or expired account token.");

        var validation = await sessionService.ValidateAsync(principal, Context.RequestAborted);
        if (validation is null)
            return AuthenticateResult.Fail("Login session is not active.");

        var user = validation.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("sub", user.Id.ToString()),
            new("login_session_id", validation.Session.Id.ToString()),
            new("device_id", validation.Session.DeviceId),
            new("expires_at", principal.ExpiresAtUtc.ToUnixTimeSeconds().ToString()),
            new("password_change_required", user.MustChangePassword ? "true" : "false")
        };
        if (!string.IsNullOrWhiteSpace(user.OrganizationId))
            claims.Add(new Claim("organization_id", user.OrganizationId));
        if (!string.IsNullOrWhiteSpace(user.Username))
            claims.Add(new Claim("username", user.Username));
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        if (!string.IsNullOrWhiteSpace(user.StudentCode))
            claims.Add(new Claim("student_code", user.StudentCode));
        if (!string.IsNullOrWhiteSpace(user.SupabaseAuthUserId))
            claims.Add(new Claim("provider_user_id", user.SupabaseAuthUserId));

        return Success(claims);
    }

    private string? ReadBearerToken()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            return header[7..].Trim();
        return Request.Query["access_token"].FirstOrDefault();
    }

    private bool DevelopmentTokenAllowed(string token)
    {
        var auth = appOptions.Value.Auth;
        var developmentToken = appOptions.Value.Server.DevelopmentTeacherToken;
        return auth.AllowDevelopmentToken
            && !string.IsNullOrWhiteSpace(developmentToken)
            && token == developmentToken
            && (environment.IsDevelopment() || string.Equals(environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase));
    }

    private AuthenticateResult Success(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, ExamTransferAuthSchemes.Account);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ExamTransferAuthSchemes.Account);
        return AuthenticateResult.Success(ticket);
    }
}

public sealed class ExamParticipantAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISessionTokenService tokenService,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers["X-Exam-Session-Token"].FirstOrDefault()
            ?? Request.Query["access_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        var principal = tokenService.Validate(token);
        if (principal is null)
            return AuthenticateResult.Fail("Invalid or expired participant token.");

        var participant = await db.SessionParticipantsSet
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == principal.ParticipantId && x.SessionId == principal.SessionId, Context.RequestAborted);
        if (participant is null)
            return AuthenticateResult.Fail("Participant no longer exists.");
        if (!string.Equals(participant.DeviceId, principal.DeviceId, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Participant token device mismatch.");
        if (participant.Status == ParticipantStatus.Rejected)
            return AuthenticateResult.Fail("Participant has been rejected.");
        if (participant.UserId.HasValue && participant.UserId.Value != principal.UserId)
            return AuthenticateResult.Fail("Participant token account mismatch.");

        var userId = participant.UserId ?? principal.UserId;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId == Guid.Empty ? participant.Id.ToString() : userId.ToString()),
            new(ClaimTypes.Name, participant.DisplayName),
            new(ClaimTypes.Role, UserRole.Student.ToString()),
            new("sub", userId == Guid.Empty ? participant.Id.ToString() : userId.ToString()),
            new("user_id", userId.ToString()),
            new("session_id", participant.SessionId.ToString()),
            new("participant_id", participant.Id.ToString()),
            new("device_id", participant.DeviceId),
            new("student_code", participant.StudentCode),
            new("participant_status", participant.Status.ToString())
        };

        var identity = new ClaimsIdentity(claims, ExamTransferAuthSchemes.ExamParticipant);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ExamTransferAuthSchemes.ExamParticipant);
        return AuthenticateResult.Success(ticket);
    }
}
