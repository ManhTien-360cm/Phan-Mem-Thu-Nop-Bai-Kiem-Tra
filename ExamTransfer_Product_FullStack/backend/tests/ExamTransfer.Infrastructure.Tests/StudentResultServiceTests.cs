using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class StudentResultServiceTests
{
    private static readonly DateTimeOffset ReturnedAt =
        new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReturnedOnlyListIsOwnedOrganizationScopedAndNeverUsesCloudFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var own = await fixture.AddEssayAsync(fixture.Student, fixture.OrganizationId, GradingStatus.Returned, ReturnedAt);
        await fixture.AddEssayAsync(fixture.Student, fixture.OrganizationId, GradingStatus.Graded, null);
        await fixture.AddEssayAsync(fixture.OtherStudent, fixture.OrganizationId, GradingStatus.Returned, ReturnedAt.AddMinutes(-1));
        await fixture.AddEssayAsync(fixture.Student, Guid.NewGuid().ToString("D"), GradingStatus.Returned, ReturnedAt.AddMinutes(-2));
        await fixture.AddEssayAsync(
            fixture.Student,
            fixture.OrganizationId,
            GradingStatus.Returned,
            ReturnedAt.AddMinutes(-3),
            SessionAccessMode.PublicCloud);

        var page = await fixture.Service.GetReturnedAsync(
            fixture.Student.Id,
            fixture.OrganizationId,
            50,
            null,
            CancellationToken.None);

        var result = Assert.Single(page.Items);
        Assert.Equal(own.Submission.Id, result.SubmissionId);
        Assert.Equal(StudentResultType.EssayFile, result.ResultType);
        Assert.Equal(StudentResultStatus.Returned, result.Status);
        Assert.Null(result.AttemptId);
        Assert.Null(result.QuizSummary);
        Assert.Equal(2, result.AttemptNumber);
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal("feedback.pdf", attachment.FileName);
        Assert.DoesNotContain("private", attachment.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ReopenedResultDisappearsAndReturnedRevisionAppearsAgain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.AddEssayAsync(
            fixture.Student,
            fixture.OrganizationId,
            GradingStatus.Returned,
            ReturnedAt);
        Assert.Single((await fixture.FirstPageAsync()).Items);

        seeded.Grade.Status = GradingStatus.InProgress;
        seeded.Grade.ReturnedAtUtc = null;
        await fixture.Db.SaveChangesAsync();
        Assert.Empty((await fixture.FirstPageAsync()).Items);

        seeded.Grade.Status = GradingStatus.Returned;
        seeded.Grade.Score = 9m;
        seeded.Grade.ReturnedAtUtc = ReturnedAt.AddHours(1);
        await fixture.Db.SaveChangesAsync();
        var returnedAgain = Assert.Single((await fixture.FirstPageAsync()).Items);
        Assert.Equal(9m, returnedAgain.Score);
        Assert.Equal(ReturnedAt.AddHours(1), returnedAgain.ReturnedAtUtc);
    }

    [Fact]
    public async Task QuizUsesPersistedAttemptNumberAndAuthoritativeAggregateWithoutAnswerKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempt = await fixture.AddQuizAsync(ReturnedAt, attemptNumber: 3);

        var result = Assert.Single((await fixture.FirstPageAsync()).Items);
        Assert.Equal(StudentResultType.Quiz, result.ResultType);
        Assert.Equal(attempt.Id, result.AttemptId);
        Assert.Null(result.SubmissionId);
        Assert.Equal(3, result.AttemptNumber);
        Assert.Empty(result.Attachments);
        var summary = Assert.IsType<StudentQuizResultSummaryDto>(result.QuizSummary);
        Assert.Equal(2, summary.TotalQuestions);
        Assert.Equal(1, summary.AnsweredQuestions);
        Assert.Equal(1, summary.CorrectCount);
        Assert.Equal(0, summary.IncorrectCount);
        Assert.Equal(1, summary.UnansweredCount);
        Assert.Equal(5m, summary.EarnedPoints);
        Assert.Equal(10m, summary.MaxPoints);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("\"questionText\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"choiceIds\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"answerKey\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"correctOption\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CursorIsStableAtEqualTimestampAndInvalidActorsFailClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddEssayAsync(fixture.Student, fixture.OrganizationId, GradingStatus.Returned, ReturnedAt);
        await fixture.AddQuizAsync(ReturnedAt, attemptNumber: 1);

        var first = await fixture.Service.GetReturnedAsync(
            fixture.Student.Id,
            fixture.OrganizationId,
            1,
            null,
            CancellationToken.None);
        var cursor = Assert.IsType<StudentResultCursorDto>(first.NextCursor);
        var second = await fixture.Service.GetReturnedAsync(
            fixture.Student.Id,
            fixture.OrganizationId,
            1,
            cursor,
            CancellationToken.None);
        Assert.Null(second.NextCursor);
        Assert.NotEqual(ResultId(first.Items[0]), ResultId(second.Items[0]));
        Assert.Equal(StudentResultType.EssayFile, first.Items[0].ResultType);
        Assert.Equal(StudentResultType.Quiz, second.Items[0].ResultType);

        var teacherError = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.GetReturnedAsync(
                fixture.Teacher.Id,
                fixture.OrganizationId,
                50,
                null,
                CancellationToken.None));
        Assert.Equal(403, teacherError.StatusCode);

        fixture.Student.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        var inactiveError = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.GetReturnedAsync(
                fixture.Student.Id,
                fixture.OrganizationId,
                50,
                null,
                CancellationToken.None));
        Assert.Equal(403, inactiveError.StatusCode);
    }

    private static Guid ResultId(StudentResultDto value) =>
        value.SubmissionId ?? value.AttemptId ?? Guid.Empty;

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public AppDbContext Db { get; } = db;
        public StudentResultService Service { get; } = new(db);
        public string OrganizationId { get; } = Guid.NewGuid().ToString("D");
        public User Student { get; private set; } = null!;
        public User OtherStudent { get; private set; } = null!;
        public User Teacher { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, db);
            fixture.Student = fixture.User("student", UserRole.Student, fixture.OrganizationId);
            fixture.OtherStudent = fixture.User("other", UserRole.Student, fixture.OrganizationId);
            fixture.Teacher = fixture.User("teacher", UserRole.Teacher, fixture.OrganizationId);
            db.UsersSet.AddRange(fixture.Student, fixture.OtherStudent, fixture.Teacher);
            await db.SaveChangesAsync();
            return fixture;
        }

        public Task<StudentResultPageDto> FirstPageAsync() => Service.GetReturnedAsync(
            Student.Id,
            OrganizationId,
            50,
            null,
            CancellationToken.None);

        public async Task<EssaySeed> AddEssayAsync(
            User participantUser,
            string ownerOrganizationId,
            GradingStatus status,
            DateTimeOffset? returnedAt,
            SessionAccessMode accessMode = SessionAccessMode.LanOnly)
        {
            var owner = User($"owner-{Guid.NewGuid():N}", UserRole.Teacher, ownerOrganizationId);
            var exam = new Exam
            {
                Title = "Essay result",
                DeliveryType = ExamDeliveryType.FileSubmission,
                CreatedBy = owner.Id
            };
            var session = new ExamSession
            {
                Exam = exam,
                ExamId = exam.Id,
                DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission,
                AccessMode = accessMode
            };
            var participant = new SessionParticipant
            {
                Session = session,
                SessionId = session.Id,
                UserId = participantUser.Id,
                StudentCode = participantUser.StudentCode!,
                DisplayName = participantUser.DisplayName,
                Status = ParticipantStatus.Approved
            };
            var submission = new Submission
            {
                Session = session,
                SessionId = session.Id,
                Participant = participant,
                ParticipantId = participant.Id,
                AttemptNumber = 2,
                Status = SubmissionStatus.Submitted,
                IsOfficial = true,
                SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan"
            };
            var grade = new Grade
            {
                Submission = submission,
                SubmissionId = submission.Id,
                Status = status,
                Score = status == GradingStatus.Returned ? 8m : 7m,
                MaxScore = 10m,
                GeneralComment = "Good",
                ReturnedAtUtc = returnedAt,
                SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan",
                Revision = 1
            };
            grade.Attachments.Add(new GradedAttachment
            {
                Grade = grade,
                GradeId = grade.Id,
                OriginalName = "C:\\private\\feedback.pdf",
                MimeType = "application/pdf",
                SizeBytes = 123
            });
            Db.AddRange(owner, exam, session, participant, submission, grade);
            await Db.SaveChangesAsync();
            return new(submission, grade);
        }

        public async Task<QuizAttempt> AddQuizAsync(DateTimeOffset returnedAt, int attemptNumber)
        {
            var exam = new Exam
            {
                Title = "Quiz result",
                DeliveryType = ExamDeliveryType.MultipleChoice,
                CreatedBy = Teacher.Id
            };
            var first = Question(exam, 1, 5m);
            var second = Question(exam, 2, 5m);
            var session = new ExamSession
            {
                Exam = exam,
                ExamId = exam.Id,
                DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
                ExamVersionSnapshot = 1,
                AccessMode = SessionAccessMode.LanOnly
            };
            var participant = new SessionParticipant
            {
                Session = session,
                SessionId = session.Id,
                UserId = Student.Id,
                StudentCode = Student.StudentCode!,
                DisplayName = Student.DisplayName,
                Status = ParticipantStatus.Approved
            };
            var snapshot = new[] { ToDto(first), ToDto(second) };
            var attempt = new QuizAttempt
            {
                Session = session,
                SessionId = session.Id,
                Participant = participant,
                ParticipantId = participant.Id,
                AttemptNumber = attemptNumber,
                ExamVersion = 1,
                Status = QuizAttemptStatus.Finalized,
                GradingStatus = GradingStatus.Returned,
                Score = 5m,
                AutoScore = 5m,
                MaxScore = 10m,
                ReturnedAtUtc = returnedAt,
                SnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            attempt.Answers.Add(new QuizAnswer
            {
                Attempt = attempt,
                AttemptId = attempt.Id,
                Question = first,
                QuestionId = first.Id,
                ChoiceIdsJson = JsonSerializer.Serialize(new[] { first.Choices.Single(x => x.IsCorrect).Id }),
                Revision = 1
            });
            Db.AddRange(exam, first, second, session, participant, attempt);
            await Db.SaveChangesAsync();
            return attempt;
        }

        private User User(string name, UserRole role, string organizationId) => new()
        {
            Username = name,
            DisplayName = name,
            StudentCode = role == UserRole.Student ? name : null,
            Role = role,
            OrganizationId = organizationId,
            IsActive = true
        };

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
                Text = "Correct",
                IsCorrect = true
            });
            question.Choices.Add(new QuizChoice
            {
                Question = question,
                QuestionId = question.Id,
                Order = 2,
                Text = "Wrong",
                IsCorrect = false
            });
            return question;
        }

        private static QuizQuestionDto ToDto(QuizQuestion value) => new(
            value.Id,
            value.Text,
            value.Order,
            value.Points,
            value.Multiple,
            value.Choices.OrderBy(x => x.Order)
                .Select(x => new QuizChoiceDto(x.Id, x.Text, x.Order))
                .ToArray());

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed record EssaySeed(Submission Submission, Grade Grade);
}
