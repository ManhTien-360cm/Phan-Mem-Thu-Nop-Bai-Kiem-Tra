using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class EssayGradeServiceTests
{
    [Fact]
    public async Task EssayGrade_SaveUsesServerMaxScoreAndEnforcesScoreRubricAndRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requestId = Guid.NewGuid();
        var saved = await fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new SaveGradeRequest(
                8.5m,
                999m,
                [new("content", "Nội dung", 8.5m, 10m, "Tốt", 1)],
                "Nhận xét",
                "new",
                requestId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);

        Assert.Equal(10m, saved.MaxScore);
        Assert.Equal(GradingStatus.Graded, saved.Status);
        Assert.Equal(1, saved.Revision);
        Assert.NotNull(saved.GradeId);
        Assert.NotEqual("new", saved.RowVersion);
        Assert.Single(saved.RubricScores);

        var retried = await fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new SaveGradeRequest(
                8.5m,
                1m,
                [new("content", "Nội dung", 8.5m, 10m, "Tốt", 1)],
                "Nhận xét",
                "new",
                requestId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        Assert.Equal(saved.RowVersion, retried.RowVersion);
        Assert.Equal(saved.Revision, retried.Revision);
        Assert.Equal(saved.Score, retried.Score);
        Assert.Equal(saved.RubricScores, retried.RubricScores);
        Assert.Single(await fixture.Db.GradesSet.ToListAsync());
        Assert.Single(await fixture.Db.EssayGradeMutationReceiptsSet.ToListAsync());

        await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(-0.01m, 10m, [], null, saved.RowVersion),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(10.01m, 10m, [], null, saved.RowVersion),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(8m, 10m, [new("dup", "A", 4m, 5m, null, 1), new("dup", "B", 4m, 5m, null, 2)], null, saved.RowVersion),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default));
    }

    [Fact]
    public async Task EssayGrade_RejectsQuizDraftWrongScopeAndUnauthorizedActors()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Session.DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice;
        fixture.Exam.DeliveryType = ExamDeliveryType.MultipleChoice;
        await fixture.Db.SaveChangesAsync();
        await AssertStatusAsync(409, () => fixture.SaveDefaultAsync(fixture.Owner));

        fixture.Session.DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission;
        fixture.Exam.DeliveryType = ExamDeliveryType.FileSubmission;
        fixture.Submission.IsOfficial = false;
        await fixture.Db.SaveChangesAsync();
        await AssertStatusAsync(409, () => fixture.SaveDefaultAsync(fixture.Owner));

        fixture.Submission.IsOfficial = true;
        await fixture.Db.SaveChangesAsync();
        await AssertStatusAsync(403, () => fixture.Service.SaveAsync(
            fixture.Submission.Id,
            fixture.DefaultSave(),
            fixture.Owner.Id,
            "org-b",
            default));
        await AssertStatusAsync(403, () => fixture.SaveDefaultAsync(fixture.OtherTeacher));
        await AssertStatusAsync(403, () => fixture.SaveDefaultAsync(fixture.Student));
        await AssertStatusAsync(403, () => fixture.Service.SaveAsync(
            fixture.Submission.Id,
            fixture.DefaultSave(),
            Guid.NewGuid(),
            null,
            default));
    }

    [Fact]
    public async Task EssayGrade_RejectsParticipantSessionMismatchAndDuplicateAuthoritativeGrade()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherSession = new ExamSession
        {
            ExamId = fixture.Exam.Id,
            RoomCode = "OTHER",
            AccessMode = SessionAccessMode.LanOnly,
            DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission
        };
        fixture.Db.ExamSessionsSet.Add(otherSession);
        fixture.Participant.SessionId = otherSession.Id;
        await fixture.Db.SaveChangesAsync();
        await AssertStatusAsync(409, () => fixture.SaveDefaultAsync(fixture.Owner));

        fixture.Participant.SessionId = fixture.Session.Id;
        await fixture.Db.SaveChangesAsync();
        _ = await fixture.SaveDefaultAsync(fixture.Owner);
        fixture.Db.ChangeTracker.Clear();
        fixture.Db.GradesSet.Add(new Grade { SubmissionId = fixture.Submission.Id, MaxScore = 10m });
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task EssayGrade_StaleRevisionConflictsWithoutOverwritingNewerGrade()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.SaveDefaultAsync(fixture.Owner);
        var second = await fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(9m, 10m, [], "Mới", first.RowVersion, Guid.NewGuid()),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(3m, 10m, [], "Cũ", first.RowVersion, Guid.NewGuid()),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default));
        Assert.Equal(409, error.StatusCode);
        var current = await fixture.Service.GetTeacherAsync(
            fixture.Submission.Id,
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        Assert.Equal(9m, current.Score);
        Assert.Equal(second.Revision, current.Revision);
    }

    [Fact]
    public async Task EssayGrade_ReturnAndReopenAreAtomicIdempotentAndPreserveFeedback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(8m, 10m, [new("content", "Nội dung", 8m, 10m, "Ổn", 1)], "Nhận xét", "new", Guid.NewGuid()),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        fixture.Db.GradedAttachmentsSet.Add(new GradedAttachment
        {
            GradeId = saved.GradeId!.Value,
            OriginalName = "feedback.pdf",
            StoredName = "feedback.pdf",
            RelativePath = "feedback.pdf",
            SizeBytes = 12,
            Sha256 = new string('a', 64),
            MimeType = "application/pdf"
        });
        await fixture.Db.SaveChangesAsync();

        var returnId = Guid.NewGuid();
        var returned = await fixture.Service.ReturnAsync(
            fixture.Submission.Id,
            new("Đã trả", saved.RowVersion, returnId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        Assert.Equal(GradingStatus.Returned, returned.Status);
        Assert.NotNull(returned.ReturnedAtUtc);
        Assert.Equal(2, returned.Revision);
        Assert.Single(returned.Attachments);
        var retriedReturn = await fixture.Service.ReturnAsync(
            fixture.Submission.Id,
            new("Đã trả", saved.RowVersion, returnId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        Assert.Equal(returned.RowVersion, retriedReturn.RowVersion);
        Assert.Equal(returned.Revision, retriedReturn.Revision);
        Assert.Equal(returned.ReturnedAtUtc, retriedReturn.ReturnedAtUtc);
        Assert.Single(await EventsAsync(fixture));

        var reopenId = Guid.NewGuid();
        var reopened = await fixture.Service.ReopenAsync(
            fixture.Submission.Id,
            new("Rà soát lại", returned.RowVersion, reopenId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        Assert.Equal(GradingStatus.Graded, reopened.Status);
        Assert.Null(reopened.ReturnedAtUtc);
        Assert.Equal(returned.Score, reopened.Score);
        Assert.Equal(returned.GeneralComment, reopened.GeneralComment);
        Assert.Equal(returned.RubricScores, reopened.RubricScores);
        Assert.Equal(returned.Attachments, reopened.Attachments);
        Assert.Equal(3, reopened.Revision);
        var retriedReopen = await fixture.Service.ReopenAsync(
            fixture.Submission.Id,
            new("Rà soát lại", returned.RowVersion, reopenId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        Assert.Equal(reopened.RowVersion, retriedReopen.RowVersion);
        Assert.Equal(reopened.Revision, retriedReopen.Revision);
        Assert.Equal(reopened.Status, retriedReopen.Status);

        var events = await EventsAsync(fixture);
        Assert.Equal(2, events.Count);
        Assert.Contains("\"eventType\":\"GradeReturned\"", events[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"eventType\":\"GradeReopened\"", events[1].PayloadJson, StringComparison.Ordinal);
        Assert.All(events, item =>
        {
            Assert.Contains(fixture.Submission.Id.ToString(), item.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"attemptId\":null", item.PayloadJson, StringComparison.Ordinal);
        });
        await AssertStatusAsync(409, () => fixture.Service.ReopenAsync(
            fixture.Submission.Id,
            new("Không hợp lệ", reopened.RowVersion, Guid.NewGuid()),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default));
    }

    [Fact]
    public async Task EssayGrade_RollbackLeavesNeitherGradeNorNotification()
    {
        await using var fixture = await Fixture.CreateAsync(audit: new ThrowingAudit());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SaveDefaultAsync(fixture.Owner));
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.GradesSet.ToListAsync());
        Assert.Empty(await EventsAsync(fixture));
        Assert.Empty(await fixture.Db.EssayGradeMutationReceiptsSet.ToListAsync());
    }

    [Fact]
    public async Task EssayGrade_PublicCloudUsesRpcAndNeverCreatesOnlyLanEvent()
    {
        var cloud = new EssayCloudAdapter();
        await using var fixture = await Fixture.CreateAsync(SessionAccessMode.PublicCloud, cloud: cloud);
        var requestId = Guid.NewGuid();
        var saved = await fixture.Service.SaveAsync(
            fixture.Submission.Id,
            new(7m, 999m, [], "Cloud", "0", requestId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        var returnId = Guid.NewGuid();
        var returned = await fixture.Service.ReturnAsync(
            fixture.Submission.Id,
            new("Cloud return", saved.RowVersion, returnId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);
        var reopenId = Guid.NewGuid();
        var reopened = await fixture.Service.ReopenAsync(
            fixture.Submission.Id,
            new("Cloud reopen", returned.RowVersion, reopenId),
            fixture.Owner.Id,
            fixture.Owner.OrganizationId,
            default);

        Assert.Equal(1, cloud.SaveCalls);
        Assert.Equal(1, cloud.ReturnCalls);
        Assert.Equal(1, cloud.ReopenCalls);
        Assert.Equal(requestId, cloud.LastSaveRequestId);
        Assert.Equal(returnId, cloud.LastReturnRequestId);
        Assert.Equal(reopenId, cloud.LastReopenRequestId);
        Assert.Equal(GradingStatus.Graded, reopened.Status);
        Assert.Empty(await EventsAsync(fixture));
        Assert.Empty(await fixture.Db.SyncQueueSet.Where(x => x.EntityType == "grades").ToListAsync());
    }

    private static async Task<List<SyncQueueItem>> EventsAsync(Fixture fixture) =>
        (await fixture.Db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
            .ToListAsync())
        .OrderBy(x => x.CreatedAtUtc)
        .ToList();

    private static async Task AssertStatusAsync(int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(
            SqliteConnection connection,
            AppDbContext db,
            User owner,
            User otherTeacher,
            User student,
            Exam exam,
            ExamSession session,
            SessionParticipant participant,
            Submission submission,
            GradeService service)
        {
            this.connection = connection;
            Db = db;
            Owner = owner;
            OtherTeacher = otherTeacher;
            Student = student;
            Exam = exam;
            Session = session;
            Participant = participant;
            Submission = submission;
            Service = service;
        }

        public AppDbContext Db { get; }
        public User Owner { get; }
        public User OtherTeacher { get; }
        public User Student { get; }
        public Exam Exam { get; }
        public ExamSession Session { get; }
        public SessionParticipant Participant { get; }
        public Submission Submission { get; }
        public GradeService Service { get; }

        public static async Task<Fixture> CreateAsync(
            SessionAccessMode accessMode = SessionAccessMode.LanOnly,
            IAuditService? audit = null,
            ICloudAdapter? cloud = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            var owner = User("owner", UserRole.Teacher, "org-a");
            var other = User("other", UserRole.Teacher, "org-b");
            var student = User("student", UserRole.Student, "org-a");
            var exam = new Exam
            {
                Title = "Essay",
                Subject = "Test",
                DurationMinutes = 30,
                DeliveryType = ExamDeliveryType.FileSubmission,
                CreatedBy = owner.Id
            };
            var session = new ExamSession
            {
                Exam = exam,
                RoomCode = "ESSAY",
                AccessMode = accessMode,
                DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission,
                Status = SessionStatus.Finished
            };
            var participant = new SessionParticipant
            {
                Session = session,
                StudentCode = "S1",
                DisplayName = "Student",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "1",
                Status = ParticipantStatus.Approved,
                SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan"
            };
            var submission = new Submission
            {
                Session = session,
                Participant = participant,
                AttemptNumber = 1,
                IdempotencyKey = "essay",
                Status = SubmissionStatus.Submitted,
                ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
                ServerReceivedAtUtc = DateTimeOffset.UtcNow,
                DeadlineUtc = DateTimeOffset.UtcNow,
                IsOfficial = true,
                SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan"
            };
            submission.Files.Add(new SubmissionFile
            {
                ClientFileId = "file",
                OriginalName = "answer.zip",
                StoredName = "answer.zip",
                RelativePath = "answer.zip",
                SizeBytes = 1,
                TransferStatus = accessMode == SessionAccessMode.PublicCloud
                    ? TransferStatus.Queued
                    : TransferStatus.Completed,
                SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan"
            });
            db.AddRange(owner, other, student, exam, session, participant, submission);
            await db.SaveChangesAsync();
            if (cloud is EssayCloudAdapter essayCloud)
                essayCloud.Bind(submission);
            var service = new GradeService(
                db,
                null!,
                null!,
                audit ?? new AuditService(db, new HttpContextAccessor()),
                new OutboxService(db),
                cloud);
            return new(connection, db, owner, other, student, exam, session, participant, submission, service);
        }

        public SaveGradeRequest DefaultSave() => new(8m, 10m, [], "OK", "new", Guid.NewGuid());

        public Task<GradeDto> SaveDefaultAsync(User actor) => Service.SaveAsync(
            Submission.Id,
            DefaultSave(),
            actor.Id,
            actor.OrganizationId,
            default);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static User User(string username, UserRole role, string organizationId) => new()
        {
            Username = username,
            DisplayName = username,
            Role = role,
            OrganizationId = organizationId,
            IsActive = true
        };
    }

    private sealed class ThrowingAudit : IAuditService
    {
        public Task WriteAsync(
            string action,
            string entityType,
            string? entityId,
            Guid? sessionId,
            object? before,
            object? after,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated audit failure");
    }

    private sealed class EssayCloudAdapter : RecordingCloudAdapter, ICloudAdapter
    {
        private CloudEssayGradeResult? result;
        private Guid sessionId;
        private Guid participantId;

        public int SaveCalls { get; private set; }
        public int ReturnCalls { get; private set; }
        public int ReopenCalls { get; private set; }
        public Guid? LastSaveRequestId { get; private set; }
        public Guid? LastReturnRequestId { get; private set; }
        public Guid? LastReopenRequestId { get; private set; }

        public void Bind(Submission submission)
        {
            sessionId = submission.SessionId;
            participantId = submission.ParticipantId;
        }

        Task<CloudEssayGradeResult> ICloudAdapter.GetPublicEssayGradeAsync(
            Guid submissionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result ?? Empty(submissionId));

        Task<CloudEssayGradeResult> ICloudAdapter.SavePublicEssayGradeAsync(
            Guid submissionId,
            decimal? score,
            IReadOnlyList<RubricScoreDto> rubricScores,
            string? generalComment,
            long expectedCloudVersion,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            LastSaveRequestId = requestId;
            var current = result ?? Empty(submissionId);
            result = current with
            {
                GradeId = current.GradeId ?? Guid.NewGuid(),
                Score = score,
                Status = GradingStatus.Graded,
                GeneralComment = generalComment,
                GradedAtUtc = DateTimeOffset.UtcNow,
                Revision = current.Revision + 1,
                CloudVersion = current.CloudVersion + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                RubricScores = rubricScores
            };
            return Task.FromResult(result);
        }

        Task<CloudEssayGradeResult> ICloudAdapter.ReturnPublicEssayGradeAsync(
            Guid submissionId,
            string? message,
            long expectedCloudVersion,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            ReturnCalls++;
            LastReturnRequestId = requestId;
            result = result! with
            {
                Status = GradingStatus.Returned,
                ReturnedAtUtc = DateTimeOffset.UtcNow,
                Revision = result!.Revision + 1,
                CloudVersion = result.CloudVersion + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        Task<CloudEssayGradeResult> ICloudAdapter.ReopenPublicEssayGradeAsync(
            Guid submissionId,
            string reason,
            long expectedCloudVersion,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            ReopenCalls++;
            LastReopenRequestId = requestId;
            result = result! with
            {
                Status = GradingStatus.Graded,
                ReturnedAtUtc = null,
                Revision = result!.Revision + 1,
                CloudVersion = result.CloudVersion + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        private CloudEssayGradeResult Empty(Guid submissionId) => new(
            null,
            submissionId,
            sessionId,
            participantId,
            null,
            10m,
            GradingStatus.NotGraded,
            null,
            null,
            null,
            null,
            0,
            0,
            DateTimeOffset.UtcNow,
            [],
            []);
    }
}
