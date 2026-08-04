using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class OnlyLanRealtimeOutboxTests
{
    [Fact]
    public async Task ParticipantEvent_DispatchesPersistedIdentityAndMarksDeliveredAfterSuccess()
    {
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.LanOnly);
        var notification = OnlyLanStudentNotificationOutbox.Enqueue(
            fixture.Db,
            StudentNotificationEventType.ParticipantApproved,
            fixture.Session.Id,
            17,
            participantId: fixture.Participant.Id);
        await fixture.Db.SaveChangesAsync();

        var delivered = await fixture.Dispatcher.DispatchPendingAsync();

        Assert.Equal(1, delivered);
        var sent = Assert.Single(fixture.Transport.Participant);
        Assert.Equal(notification.EventId, sent.EventId);
        Assert.Equal(17, sent.Revision);
        Assert.Equal(fixture.Participant.Id, sent.ParticipantId);
        var row = await fixture.Db.SyncQueueSet.SingleAsync(x => x.Id == notification.EventId);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.NotNull(row.CompletedAtUtc);
    }

    [Fact]
    public async Task TransportFailure_RemainsRetryableAndRetryKeepsEventIdAndRevision()
    {
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.LanOnly);
        fixture.Transport.FailuresRemaining = 1;
        var notification = OnlyLanStudentNotificationOutbox.Enqueue(
            fixture.Db,
            StudentNotificationEventType.TeacherMessageReceived,
            fixture.Session.Id,
            23,
            message: "Room notice");
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(0, await fixture.Dispatcher.DispatchPendingAsync());
        var failed = await fixture.Db.SyncQueueSet.SingleAsync(x => x.Id == notification.EventId);
        Assert.Equal(SyncStatus.LocalOnly, failed.Status);
        Assert.Equal(1, failed.RetryCount);
        failed.NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Dispatcher.DispatchPendingAsync());
        var sent = Assert.Single(fixture.Transport.Session);
        Assert.Equal(notification.EventId, sent.EventId);
        Assert.Equal(notification.Revision, sent.Revision);
    }

    [Fact]
    public async Task PublicCloudSession_FailsClosedWithoutUsingOnlyLanTransport()
    {
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.PublicCloud);
        var notification = OnlyLanStudentNotificationOutbox.Enqueue(
            fixture.Db,
            StudentNotificationEventType.ParticipantApproved,
            fixture.Session.Id,
            1,
            participantId: fixture.Participant.Id);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(0, await fixture.Dispatcher.DispatchPendingAsync());

        Assert.Empty(fixture.Transport.Participant);
        Assert.Empty(fixture.Transport.Session);
        var row = await fixture.Db.SyncQueueSet.SingleAsync(x => x.Id == notification.EventId);
        Assert.Equal(SyncStatus.Conflict, row.Status);
        Assert.Equal("ONLYLAN_REALTIME_SESSION_SCOPE_INVALID", row.LastError);
    }

    [Fact]
    public async Task TransactionRollback_RemovesMutationAndNotificationTogether()
    {
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.LanOnly);
        var originalSequence = fixture.Session.Sequence;
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync())
        {
            fixture.Session.Sequence++;
            OnlyLanStudentNotificationOutbox.Enqueue(
                fixture.Db,
                StudentNotificationEventType.ParticipantApproved,
                fixture.Session.Id,
                fixture.Session.Sequence,
                participantId: fixture.Participant.Id);
            await fixture.Db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(originalSequence, (await fixture.Db.ExamSessionsSet.SingleAsync()).Sequence);
        Assert.Empty(await fixture.Db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
            .ToListAsync());
        Assert.Empty(fixture.Transport.Participant);
    }

    [Fact]
    public async Task Replay_SendsOnlySessionBroadcastAndMatchingParticipantEvents()
    {
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.LanOnly);
        var peer = new SessionParticipant
        {
            Session = fixture.Session,
            StudentCode = "S-PEER",
            DisplayName = "Peer",
            DeviceId = "peer-device",
            Status = ParticipantStatus.Approved
        };
        fixture.Db.SessionParticipantsSet.Add(peer);
        OnlyLanStudentNotificationOutbox.Enqueue(
            fixture.Db,
            StudentNotificationEventType.TeacherMessageReceived,
            fixture.Session.Id,
            2,
            message: "Broadcast");
        OnlyLanStudentNotificationOutbox.Enqueue(
            fixture.Db,
            StudentNotificationEventType.ParticipantApproved,
            fixture.Session.Id,
            3,
            participantId: fixture.Participant.Id);
        OnlyLanStudentNotificationOutbox.Enqueue(
            fixture.Db,
            StudentNotificationEventType.ParticipantApproved,
            fixture.Session.Id,
            4,
            participantId: peer.Id);
        await fixture.Db.SaveChangesAsync();
        await fixture.Dispatcher.DispatchPendingAsync();

        var replayed = await fixture.Dispatcher.ReplayAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            "student-connection");

        Assert.Equal(2, replayed);
        Assert.Equal(2, fixture.Transport.Connection.Count);
        Assert.All(fixture.Transport.Connection, x => Assert.Equal("student-connection", x.ConnectionId));
        Assert.DoesNotContain(fixture.Transport.Connection, x => x.Notification.ParticipantId == peer.Id);
    }

    [Fact]
    public async Task EssayGradeReturnAndReopen_EnqueueTypedSubmissionEvents()
    {
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.LanOnly);
        var submission = new Submission
        {
            Session = fixture.Session,
            Participant = fixture.Participant,
            AttemptNumber = 1,
            IdempotencyKey = "essay-grade",
            Status = SubmissionStatus.Submitted,
            ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            IsOfficial = true
        };
        var grade = new Grade
        {
            Submission = submission,
            Status = GradingStatus.Graded,
            Score = 8m,
            MaxScore = 10m,
            GradedAtUtc = DateTimeOffset.UtcNow
        };
        submission.Files.Add(new SubmissionFile
        {
            ClientFileId = "essay-file",
            OriginalName = "answer.zip",
            StoredName = "answer.zip",
            RelativePath = "answer.zip",
            SizeBytes = 1,
            TransferStatus = TransferStatus.Completed
        });
        fixture.Db.AddRange(submission, grade);
        await fixture.Db.SaveChangesAsync();
        var service = new GradeService(
            fixture.Db,
            null!,
            null!,
            new AuditService(fixture.Db, new HttpContextAccessor()),
            new OutboxService(fixture.Db));

        var returned = await service.ReturnAsync(
            submission.Id,
            new ReturnGradeRequest("Published"),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);
        await service.ReopenAsync(
            submission.Id,
            new ReopenGradeRequest("Review", returned.RowVersion),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);

        var storedEvents = await fixture.Db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
            .ToListAsync();
        var events = storedEvents.OrderBy(x => x.CreatedAtUtc).ToList();
        Assert.Equal(2, events.Count);
        Assert.Contains("\"eventType\":\"GradeReturned\"", events[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"eventType\":\"GradeReopened\"", events[1].PayloadJson, StringComparison.Ordinal);
        Assert.All(events, x => Assert.Contains(submission.Id.ToString(), x.PayloadJson, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(
            SqliteConnection connection,
            AppDbContext db,
            User teacher,
            ExamSession session,
            SessionParticipant participant,
            RecordingTransport transport)
        {
            this.connection = connection;
            Db = db;
            Teacher = teacher;
            Session = session;
            Participant = participant;
            Transport = transport;
            Dispatcher = new(
                db,
                transport,
                NullLogger<OnlyLanStudentNotificationDispatcher>.Instance);
        }

        public AppDbContext Db { get; }
        public User Teacher { get; }
        public ExamSession Session { get; }
        public SessionParticipant Participant { get; }
        public RecordingTransport Transport { get; }
        public OnlyLanStudentNotificationDispatcher Dispatcher { get; }

        public static async Task<Fixture> CreateAsync(SessionAccessMode mode)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            var teacher = new User
            {
                Username = "teacher",
                DisplayName = "Teacher",
                Role = UserRole.Teacher,
                OrganizationId = "org-a"
            };
            var session = new ExamSession
            {
                Exam = new Exam
                {
                    Title = "Realtime",
                    Subject = "Test",
                    DurationMinutes = 30,
                    DeliveryType = ExamDeliveryType.FileSubmission,
                    CreatedBy = teacher.Id
                },
                RoomCode = "RT-OUTBOX",
                HostDeviceId = "teacher",
                AccessMode = mode,
                DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission
            };
            var participant = new SessionParticipant
            {
                Session = session,
                StudentCode = "S-001",
                DisplayName = "Student",
                DeviceId = "student-device",
                Status = ParticipantStatus.Approved
            };
            db.AddRange(teacher, session, participant);
            await db.SaveChangesAsync();
            return new(connection, db, teacher, session, participant, new RecordingTransport());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingTransport : IOnlyLanStudentNotificationTransport
    {
        public int FailuresRemaining { get; set; }
        public List<StudentNotificationEventDto> Session { get; } = [];
        public List<StudentNotificationEventDto> Participant { get; } = [];
        public List<(string ConnectionId, StudentNotificationEventDto Notification)> Connection { get; } = [];

        public Task PublishSessionAsync(StudentNotificationEventDto notification, CancellationToken cancellationToken = default)
        {
            FailIfRequested();
            Session.Add(notification);
            return Task.CompletedTask;
        }

        public Task PublishParticipantAsync(StudentNotificationEventDto notification, CancellationToken cancellationToken = default)
        {
            FailIfRequested();
            Participant.Add(notification);
            return Task.CompletedTask;
        }

        public Task PublishConnectionAsync(string connectionId, StudentNotificationEventDto notification, CancellationToken cancellationToken = default)
        {
            FailIfRequested();
            Connection.Add((connectionId, notification));
            return Task.CompletedTask;
        }

        private void FailIfRequested()
        {
            if (FailuresRemaining <= 0) return;
            FailuresRemaining--;
            throw new IOException("simulated transport failure");
        }
    }
}
