using System.Text.Json;
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

public sealed class QuizGradingServiceTests
{
    [Fact]
    public async Task Save_UsesWeightedAuthoritativeAnswersAndRejectsForgedScore()
    {
        await using var fixture = await Fixture.CreateAsync();
        var current = await fixture.Service.GetAsync(
            fixture.Attempt.Id,
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);

        var saved = await fixture.Service.SaveAsync(
            fixture.Attempt.Id,
            new(null, "  authoritative  ", current.RowVersion, Guid.NewGuid()),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);

        Assert.Equal(2.50m, saved.AutoScore);
        Assert.Equal(2.50m, saved.Score);
        Assert.Equal(10.00m, saved.MaxScore);
        Assert.Equal("authoritative", saved.GeneralComment);
        Assert.Equal(2.50m, saved.Questions.Single(x => x.Order == 1).EarnedPoints);
        Assert.Equal(0m, saved.Questions.Single(x => x.Order == 2).EarnedPoints);
        Assert.Equal(0m, saved.Questions.Single(x => x.Order == 3).EarnedPoints);

        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Service.SaveAsync(
            fixture.Attempt.Id,
            new(9.99m, null, saved.RowVersion, Guid.NewGuid()),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default));
        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
    }

    [Fact]
    public async Task Calculator_CoversAllCorrectPartialAndUnansweredWithDecimalPrecision()
    {
        await using var fixture = await Fixture.CreateAsync();

        var partial = await QuizGradeAuthoritativeScoring.CalculateAsync(
            fixture.Db,
            fixture.Attempt,
            default);
        Assert.Equal((2.50m, 2, 1, 1, 1),
            (partial.Score, partial.AnsweredQuestions, partial.CorrectCount,
                partial.IncorrectCount, partial.UnansweredCount));

        fixture.Db.QuizAnswersSet.RemoveRange(fixture.Attempt.Answers.ToList());
        await fixture.Db.SaveChangesAsync();
        fixture.Attempt.Answers.Clear();
        var unanswered = await QuizGradeAuthoritativeScoring.CalculateAsync(
            fixture.Db,
            fixture.Attempt,
            default);
        Assert.Equal((0m, 0, 0, 0, 3),
            (unanswered.Score, unanswered.AnsweredQuestions, unanswered.CorrectCount,
                unanswered.IncorrectCount, unanswered.UnansweredCount));

        foreach (var question in fixture.Questions)
        {
            var correct = question.Choices.Where(x => x.IsCorrect).Select(x => x.Id).ToArray();
            var answer = new QuizAnswer
            {
                Attempt = fixture.Attempt,
                AttemptId = fixture.Attempt.Id,
                Question = question,
                QuestionId = question.Id,
                ChoiceIdsJson = JsonSerializer.Serialize(correct),
                Revision = 1,
                ClientUpdatedAtUtc = DateTimeOffset.UtcNow
            };
            fixture.Attempt.Answers.Add(answer);
            fixture.Db.QuizAnswersSet.Add(answer);
        }
        await fixture.Db.SaveChangesAsync();
        var allCorrect = await QuizGradeAuthoritativeScoring.CalculateAsync(
            fixture.Db,
            fixture.Attempt,
            default);
        Assert.Equal(10.00m, allCorrect.Score);
        Assert.Equal(3, allCorrect.CorrectCount);
    }

    [Fact]
    public async Task Mutations_RejectInvalidActorTenantEssayAndAnswerGraph()
    {
        await using (var fixture = await Fixture.CreateAsync())
        {
            var student = new User
            {
                Username = "student-grader",
                DisplayName = "Student",
                Role = UserRole.Student,
                OrganizationId = fixture.Teacher.OrganizationId
            };
            fixture.Db.UsersSet.Add(student);
            await fixture.Db.SaveChangesAsync();
            await AssertStatusAsync(403, () => fixture.Service.SaveAsync(
                fixture.Attempt.Id,
                new(null, null, fixture.Attempt.RowVersion),
                student.Id,
                student.OrganizationId,
                default));
            await AssertStatusAsync(403, () => fixture.Service.SaveAsync(
                fixture.Attempt.Id,
                new(null, null, fixture.Attempt.RowVersion),
                fixture.Teacher.Id,
                "wrong-org",
                default));
        }

        await using (var fixture = await Fixture.CreateAsync())
        {
            fixture.Exam.DeliveryType = ExamDeliveryType.FileSubmission;
            fixture.Session.DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission;
            await fixture.Db.SaveChangesAsync();
            await AssertStatusAsync(409, () => fixture.Service.SaveAsync(
                fixture.Attempt.Id,
                new(null, null, fixture.Attempt.RowVersion),
                fixture.Teacher.Id,
                fixture.Teacher.OrganizationId,
                default));
        }

        await using (var fixture = await Fixture.CreateAsync())
        {
            fixture.Attempt.Answers.First().ChoiceIdsJson = JsonSerializer.Serialize(new[] { Guid.NewGuid() });
            await fixture.Db.SaveChangesAsync();
            await AssertStatusAsync(400, () => fixture.Service.SaveAsync(
                fixture.Attempt.Id,
                new(null, null, fixture.Attempt.RowVersion),
                fixture.Teacher.Id,
                fixture.Teacher.OrganizationId,
                default));
        }
    }

    [Fact]
    public async Task ReturnAndReopen_AreIdempotentAtomicAndPreserveGrade()
    {
        await using var fixture = await Fixture.CreateAsync();
        var current = await fixture.Service.GetAsync(
            fixture.Attempt.Id,
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);
        var saved = await fixture.Service.SaveAsync(
            fixture.Attempt.Id,
            new(null, "Keep", current.RowVersion, Guid.NewGuid()),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);
        var returnId = Guid.NewGuid();
        var returned = await fixture.Service.ReturnAsync(
            fixture.Attempt.Id,
            new("Published", saved.RowVersion, returnId),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);
        var returnedRetry = await fixture.Service.ReturnAsync(
            fixture.Attempt.Id,
            new("Published", saved.RowVersion, returnId),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);

        Assert.Equal(GradingStatus.Returned, returned.Status);
        Assert.NotNull(returned.ReturnedAtUtc);
        Assert.Equal(returned.RowVersion, returnedRetry.RowVersion);
        Assert.Equal(returned.Score, returnedRetry.Score);
        Assert.Single(await fixture.Db.SyncQueueSet.Where(
            x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType).ToListAsync());
        var returnReceipt = await fixture.Db.QuizGradeMutationReceiptsSet.SingleAsync(x => x.Id == returnId);
        Assert.NotNull(returnReceipt.EventId);

        var reopenId = Guid.NewGuid();
        var reopened = await fixture.Service.ReopenAsync(
            fixture.Attempt.Id,
            new("Recheck", returned.RowVersion, reopenId),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);
        var reopenedRetry = await fixture.Service.ReopenAsync(
            fixture.Attempt.Id,
            new("Recheck", returned.RowVersion, reopenId),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default);

        Assert.Equal(GradingStatus.Graded, reopened.Status);
        Assert.Null(reopened.ReturnedAtUtc);
        Assert.Equal(returned.Score, reopened.Score);
        Assert.Equal(returned.GeneralComment, reopened.GeneralComment);
        Assert.Equal(reopened.RowVersion, reopenedRetry.RowVersion);
        Assert.Equal(2, await fixture.Db.SyncQueueSet.CountAsync(
            x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType));
        await AssertStatusAsync(409, () => fixture.Service.ReopenAsync(
            fixture.Attempt.Id,
            new("Again", reopened.RowVersion, Guid.NewGuid()),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default));
    }

    [Fact]
    public async Task SaveRollback_DoesNotLeaveGradeAuditProjectionOrReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(new ThrowingAudit());
        var before = fixture.Attempt.RowVersion;
        var requestId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(
            fixture.Attempt.Id,
            new(null, "rollback", before, requestId),
            fixture.Teacher.Id,
            fixture.Teacher.OrganizationId,
            default));

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.QuizAttemptsSet.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Attempt.Id);
        Assert.Equal(before, persisted.RowVersion);
        Assert.Equal(1m, persisted.Score);
        Assert.False(await fixture.Db.AuditLogsSet.AnyAsync(x => x.Action == "QuizGradeSaved"));
        Assert.False(await fixture.Db.SyncQueueSet.AnyAsync(x => x.EntityType == "quiz_attempts"));
        Assert.False(await fixture.Db.QuizGradeMutationReceiptsSet.AnyAsync(x => x.Id == requestId));
    }

    [Fact]
    public async Task StudentVisibility_RequiresReturnedEvenForShowAfterSubmissionPolicy()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Attempt.ResultPolicySnapshot = QuizResultPolicy.ShowAfterSubmission;
        await fixture.Db.SaveChangesAsync();

        var hidden = await fixture.Service.GetStudentReviewAsync(
            fixture.Attempt.Id,
            fixture.Participant.Id,
            default);
        Assert.False(hidden.ScoreVisible);
        Assert.Null(hidden.Score);
        Assert.All(hidden.Questions.SelectMany(x => x.Choices), x => Assert.Null(x.Correct));
    }

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
            User teacher,
            Exam exam,
            ExamSession session,
            SessionParticipant participant,
            QuizAttempt attempt,
            IReadOnlyList<QuizQuestion> questions,
            IAuditService audit)
        {
            this.connection = connection;
            Db = db;
            Teacher = teacher;
            Exam = exam;
            Session = session;
            Participant = participant;
            Attempt = attempt;
            Questions = questions;
            Service = new QuizGradingService(db, audit, new OutboxService(db));
        }

        public AppDbContext Db { get; }
        public User Teacher { get; }
        public Exam Exam { get; }
        public ExamSession Session { get; }
        public SessionParticipant Participant { get; }
        public QuizAttempt Attempt { get; }
        public IReadOnlyList<QuizQuestion> Questions { get; }
        public QuizGradingService Service { get; }

        public static async Task<Fixture> CreateAsync(IAuditService? audit = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var teacher = new User
            {
                Username = "quiz-owner",
                DisplayName = "Quiz Owner",
                Role = UserRole.Teacher,
                OrganizationId = "org-quiz"
            };
            var exam = new Exam
            {
                Title = "Weighted Quiz",
                Subject = "Test",
                DurationMinutes = 30,
                DeliveryType = ExamDeliveryType.MultipleChoice,
                CreatedBy = teacher.Id
            };
            var questions = new[]
            {
                Question(exam, 1, 2.50m),
                Question(exam, 2, 3.25m),
                Question(exam, 3, 4.25m)
            };
            foreach (var question in questions)
                exam.QuizQuestions.Add(question);
            var session = new ExamSession
            {
                Exam = exam,
                ExamId = exam.Id,
                RoomCode = "A09QUIZ",
                Status = SessionStatus.Finished,
                AccessMode = SessionAccessMode.LanOnly,
                DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
                ExamVersionSnapshot = 1,
                QuizResultPolicySnapshot = QuizResultPolicy.Hidden
            };
            var participant = new SessionParticipant
            {
                Session = session,
                SessionId = session.Id,
                StudentCode = "A09-STUDENT",
                DisplayName = "A09 Student",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "1",
                Status = ParticipantStatus.Approved
            };
            var snapshot = questions.Select(x => new QuizQuestionDto(
                x.Id,
                x.Text,
                x.Order,
                x.Points,
                x.Multiple,
                x.Choices.OrderBy(c => c.Order)
                    .Select(c => new QuizChoiceDto(c.Id, c.Text, c.Order)).ToList())).ToList();
            var attempt = new QuizAttempt
            {
                Session = session,
                SessionId = session.Id,
                Participant = participant,
                ParticipantId = participant.Id,
                ExamVersion = 1,
                Status = QuizAttemptStatus.Finalized,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                FinalizedAtUtc = DateTimeOffset.UtcNow,
                AutoScore = 1m,
                Score = 1m,
                MaxScore = 10m,
                GradingStatus = GradingStatus.Graded,
                GradedAtUtc = DateTimeOffset.UtcNow,
                SnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            attempt.Answers.Add(Answer(attempt, questions[0], questions[0].Choices.Single(x => x.IsCorrect).Id));
            attempt.Answers.Add(Answer(attempt, questions[1], questions[1].Choices.Single(x => !x.IsCorrect).Id));
            db.AddRange(teacher, exam, session, participant, attempt);
            await db.SaveChangesAsync();
            return new(
                connection,
                db,
                teacher,
                exam,
                session,
                participant,
                attempt,
                questions,
                audit ?? new AuditService(db, new HttpContextAccessor()));
        }

        private static QuizQuestion Question(Exam exam, int order, decimal points)
        {
            var question = new QuizQuestion
            {
                Exam = exam,
                ExamId = exam.Id,
                Version = 1,
                Order = order,
                Text = $"Question {order}",
                Points = points
            };
            question.Choices.Add(new QuizChoice
            {
                Question = question,
                QuestionId = question.Id,
                Order = 1,
                Text = "Wrong"
            });
            question.Choices.Add(new QuizChoice
            {
                Question = question,
                QuestionId = question.Id,
                Order = 2,
                Text = "Correct",
                IsCorrect = true
            });
            return question;
        }

        private static QuizAnswer Answer(QuizAttempt attempt, QuizQuestion question, Guid choiceId) => new()
        {
            Attempt = attempt,
            AttemptId = attempt.Id,
            Question = question,
            QuestionId = question.Id,
            ChoiceIdsJson = JsonSerializer.Serialize(new[] { choiceId }),
            Revision = 1,
            ClientUpdatedAtUtc = DateTimeOffset.UtcNow
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
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
            throw new InvalidOperationException("AUDIT_WRITE_FAILED");
    }
}
