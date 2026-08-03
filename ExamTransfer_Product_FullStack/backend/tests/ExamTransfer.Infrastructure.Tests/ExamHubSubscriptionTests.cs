using System.Security.Claims;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.LocalServer.Hubs;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class ExamHubSubscriptionTests
{
    [Theory]
    [InlineData(UserRole.Teacher)]
    [InlineData(UserRole.Admin)]
    public async Task AccountTeacherOrAdmin_CanSubscribeAndUnsubscribeExistingSession(
        UserRole role)
    {
        await using var fixture = await HubFixture.CreateAsync(AccountPrincipal(role));

        await fixture.Hub.SubscribeSession(fixture.SessionId);
        await fixture.Hub.UnsubscribeSession(fixture.SessionId);

        Assert.Contains(
            (fixture.ConnectionId, ExamHub.SessionGroup(fixture.SessionId)),
            fixture.Groups.Added);
        Assert.Contains(
            (fixture.ConnectionId, ExamHub.SessionGroup(fixture.SessionId)),
            fixture.Groups.Removed);
    }

    [Fact]
    public async Task AccountStudent_CannotSubscribeToTeacherSessionGroup()
    {
        await using var fixture = await HubFixture.CreateAsync(
            AccountPrincipal(UserRole.Student));

        var error = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.SubscribeSession(fixture.SessionId));

        Assert.Equal(ErrorCodes.Unauthorized, error.Message);
        Assert.Empty(fixture.Groups.Added);
    }

    [Fact]
    public async Task Teacher_CannotSubscribeToMissingSession()
    {
        await using var fixture = await HubFixture.CreateAsync(
            AccountPrincipal(UserRole.Teacher));

        var error = await Assert.ThrowsAsync<HubException>(
            () => fixture.Hub.SubscribeSession(Guid.NewGuid()));

        Assert.Equal(ErrorCodes.NotFound, error.Message);
        Assert.Empty(fixture.Groups.Added);
    }

    [Fact]
    public async Task Teacher_CanUnsubscribeAfterSessionWasDeleted()
    {
        await using var fixture = await HubFixture.CreateAsync(
            AccountPrincipal(UserRole.Teacher));
        var deletedSessionId = Guid.NewGuid();

        await fixture.Hub.UnsubscribeSession(deletedSessionId);

        Assert.Contains(
            (fixture.ConnectionId, ExamHub.SessionGroup(deletedSessionId)),
            fixture.Groups.Removed);
    }

    [Fact]
    public async Task ParticipantConnection_PreservesAutomaticSessionAndParticipantGroups()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var fixture = await HubFixture.CreateAsync(
            ParticipantPrincipal(sessionId, participantId),
            sessionId);

        await fixture.Hub.OnConnectedAsync();

        Assert.Contains(
            (fixture.ConnectionId, ExamHub.SessionGroup(sessionId)),
            fixture.Groups.Added);
        Assert.Contains(
            (fixture.ConnectionId, ExamHub.StudentSessionGroup(sessionId)),
            fixture.Groups.Added);
        Assert.Contains(
            (fixture.ConnectionId, ExamHub.ParticipantGroup(sessionId, participantId)),
            fixture.Groups.Added);
    }

    [Fact]
    public async Task ParticipantConnection_PublicCloudSessionIsRejectedFailClosed()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var fixture = await HubFixture.CreateAsync(
            ParticipantPrincipal(sessionId, participantId),
            sessionId);
        var session = await fixture.Db.ExamSessionsSet.SingleAsync();
        session.AccessMode = SessionAccessMode.PublicCloud;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => fixture.Hub.OnConnectedAsync());

        Assert.Equal(ErrorCodes.Unauthorized, error.Message);
        Assert.Empty(fixture.Groups.Added);
    }

    [Fact]
    public async Task ParticipantConnection_WrongSessionOrUserIsRejected()
    {
        var claimedSessionId = Guid.NewGuid();
        var actualSessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var fixture = await HubFixture.CreateAsync(
            ParticipantPrincipal(claimedSessionId, participantId),
            actualSessionId);

        var error = await Assert.ThrowsAsync<HubException>(() => fixture.Hub.OnConnectedAsync());

        Assert.Equal(ErrorCodes.Unauthorized, error.Message);
        Assert.Empty(fixture.Groups.Added);
    }

    [Fact]
    public async Task AccountTeacherCannotForgeParticipantClaims()
    {
        var identity = (ClaimsIdentity)AccountPrincipal(UserRole.Teacher).Identity!;
        identity.AddClaim(new Claim("session_id", Guid.NewGuid().ToString()));
        identity.AddClaim(new Claim("participant_id", Guid.NewGuid().ToString()));
        await using var fixture = await HubFixture.CreateAsync(new ClaimsPrincipal(identity));

        var error = await Assert.ThrowsAsync<HubException>(() => fixture.Hub.OnConnectedAsync());

        Assert.Equal(ErrorCodes.Unauthorized, error.Message);
        Assert.Empty(fixture.Groups.Added);
    }

    [Fact]
    public async Task ParticipantDisconnect_RemovesValidatedStudentGroups()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        await using var fixture = await HubFixture.CreateAsync(
            ParticipantPrincipal(sessionId, participantId),
            sessionId);
        await fixture.Hub.OnConnectedAsync();

        await fixture.Hub.OnDisconnectedAsync(null);

        Assert.Contains((fixture.ConnectionId, ExamHub.SessionGroup(sessionId)), fixture.Groups.Removed);
        Assert.Contains((fixture.ConnectionId, ExamHub.StudentSessionGroup(sessionId)), fixture.Groups.Removed);
        Assert.Contains((fixture.ConnectionId, ExamHub.ParticipantGroup(sessionId, participantId)), fixture.Groups.Removed);
    }

    private static ClaimsPrincipal AccountPrincipal(UserRole role)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role.ToString())
        ], ExamTransferAuthSchemes.Account);
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal ParticipantPrincipal(
        Guid sessionId,
        Guid participantId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, UserRole.Student.ToString()),
            new Claim("user_id", participantId.ToString()),
            new Claim("session_id", sessionId.ToString()),
            new Claim("participant_id", participantId.ToString()),
            new Claim("device_id", "student-device")
        ], ExamTransferAuthSchemes.ExamParticipant);
        return new ClaimsPrincipal(identity);
    }

    private sealed class HubFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private HubFixture(
            SqliteConnection connection,
            AppDbContext db,
            ExamHub hub,
            RecordingGroupManager groups,
            Guid sessionId,
            string connectionId)
        {
            this.connection = connection;
            Db = db;
            Hub = hub;
            Groups = groups;
            SessionId = sessionId;
            ConnectionId = connectionId;
        }

        public AppDbContext Db { get; }
        public ExamHub Hub { get; }
        public RecordingGroupManager Groups { get; }
        public Guid SessionId { get; }
        public string ConnectionId { get; }

        public static async Task<HubFixture> CreateAsync(
            ClaimsPrincipal principal,
            Guid? requestedSessionId = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var exam = new Exam
            {
                Title = "Realtime test",
                Subject = "Test",
                DurationMinutes = 30
            };
            var session = new ExamSession
            {
                Id = requestedSessionId ?? Guid.NewGuid(),
                Exam = exam,
                RoomCode = "REALTIME",
                HostDeviceId = "teacher-device"
            };
            db.ExamSessionsSet.Add(session);
            if (Guid.TryParse(principal.FindFirst("participant_id")?.Value, out var participantId))
            {
                db.SessionParticipantsSet.Add(new SessionParticipant
                {
                    Id = participantId,
                    Session = session,
                    SessionId = session.Id,
                    UserId = participantId,
                    StudentCode = "S-REALTIME",
                    DisplayName = "Realtime Student",
                    DeviceId = principal.FindFirst("device_id")?.Value ?? "student-device",
                    Status = ParticipantStatus.Approved
                });
            }
            await db.SaveChangesAsync();

            var connectionId = $"connection-{Guid.NewGuid():N}";
            var groups = new RecordingGroupManager();
            var dispatcher = new OnlyLanStudentNotificationDispatcher(
                db,
                new RecordingNotificationTransport(),
                NullLogger<OnlyLanStudentNotificationDispatcher>.Instance);
            var hub = new ExamHub(null!, null!, db, dispatcher)
            {
                Context = new TestHubCallerContext(connectionId, principal),
                Groups = groups
            };
            return new HubFixture(
                connection,
                db,
                hub,
                groups,
                session.Id,
                connectionId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingNotificationTransport : IOnlyLanStudentNotificationTransport
    {
        public Task PublishSessionAsync(StudentNotificationEventDto notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync(StudentNotificationEventDto notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishConnectionAsync(string connectionId, StudentNotificationEventDto notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> Added { get; } = [];
        public List<(string ConnectionId, string GroupName)> Removed { get; } = [];

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Removed.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }

    private sealed class TestHubCallerContext(
        string connectionId,
        ClaimsPrincipal user) : HubCallerContext
    {
        private readonly CancellationTokenSource aborted = new();

        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal User { get; } = user;
        public override IDictionary<object, object?> Items { get; } =
            new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } =
            new FeatureCollection();
        public override CancellationToken ConnectionAborted => aborted.Token;

        public override void Abort() => aborted.Cancel();
    }
}
