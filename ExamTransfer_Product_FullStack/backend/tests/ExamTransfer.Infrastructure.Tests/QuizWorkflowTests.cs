using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class QuizWorkflowTests
{
    [Fact]
    public async Task ImportSyncFinalize_IsResumableIdempotentAndServerGraded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var exam = new Exam
        {
            Title = "Quiz",
            Subject = "Test",
            DurationMinutes = 30,
            Status = ExamStatus.Draft,
            DeliveryType = ExamDeliveryType.MultipleChoice,
            SupervisionMode = SupervisionMode.Standard,
            QuizResultPolicy = QuizResultPolicy.ShowAfterSubmission
        };
        db.ExamsSet.Add(exam);
        await db.SaveChangesAsync();
        var service = new QuizService(
            db,
            new QuizProjectionOutbox(new OutboxService(db)));
        var teacherId = Guid.NewGuid();
        var preview = await service.PreviewImportAsync(
            exam.Id,
            teacherId,
            new(
                "quiz.docx",
                Convert.ToBase64String(Docx(
                    "1. 2 + 2?",
                    "A. 3",
                    "B. 4",
                    "C. 5",
                    "Đáp án đúng: B",
                    "Câu 2: Số chẵn",
                    "A. 2",
                    "B. 3",
                    "C. 4",
                    "Đáp án đúng: A; C"))),
            default);
        var imported = await service.CommitImportAsync(
            exam.Id,
            teacherId,
            new(preview.PreviewToken, false, exam.RowVersion),
            default);
        Assert.Equal(2, imported.QuestionCount);
        Assert.Equal(10m, preview.MaxScore);
        Assert.Equal(10m, imported.MaxScore);

        exam.Status = ExamStatus.Published;
        var session = new ExamSession
        {
            ExamId = exam.Id,
            Exam = exam,
            RoomCode = "QUIZ01",
            Status = SessionStatus.InProgress,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
            SupervisionModeSnapshot = SupervisionMode.Standard,
            QuizResultPolicySnapshot = QuizResultPolicy.ShowAfterSubmission,
            ExamVersionSnapshot = exam.Version
        };
        var participant = new SessionParticipant { Session = session, SessionId = session.Id, StudentCode = "S1", DisplayName = "Student", DeviceId = "d", MachineName = "m", AppVersion = "1", Status = ParticipantStatus.Approved };
        db.ExamSessionsSet.Add(session);
        db.SessionParticipantsSet.Add(participant);
        db.ControlPoliciesSet.Add(new ControlPolicy
        {
            SessionId = session.Id,
            Version = 1,
            Status = PolicyApplyStatus.Applied
        });
        db.DevicePolicyStatusesSet.Add(new DevicePolicyStatus
        {
            SessionId = session.Id,
            ParticipantId = participant.Id,
            PolicyVersion = 1,
            Status = PolicyApplyStatus.Applied
        });
        await db.SaveChangesAsync();

        var attempt = await service.StartOrGetAttemptAsync(session.Id, participant.Id, default);
        Assert.Equal(2, attempt.Questions.Count);
        Assert.DoesNotContain("correct", (await db.QuizAttemptsSet.SingleAsync()).SnapshotJson, StringComparison.OrdinalIgnoreCase);
        var q1 = attempt.Questions[0]; var q2 = attempt.Questions[1];
        await service.SyncAnswersAsync(attempt.Id, participant.Id, new([
            new(q1.Id, [q1.Choices[1].Id], 2, DateTimeOffset.UtcNow),
            new(q2.Id, [q2.Choices[0].Id, q2.Choices[2].Id], 1, DateTimeOffset.UtcNow)
        ]), default);
        var stale = await service.SyncAnswersAsync(attempt.Id, participant.Id, new([
            new(q1.Id, [q1.Choices[0].Id], 1, DateTimeOffset.UtcNow)
        ]), default);
        Assert.Equal(q1.Choices[1].Id, stale.Answers.Single(x => x.QuestionId == q1.Id).ChoiceIds.Single());

        var finalized = await service.FinalizeAsync(attempt.Id, participant.Id, new("final-1", DateTimeOffset.UtcNow), default);
        var repeated = await service.FinalizeAsync(attempt.Id, participant.Id, new("final-1", DateTimeOffset.UtcNow), default);
        Assert.False(finalized.ScoreVisible);
        Assert.Null(finalized.Score);
        Assert.Equal(10m, finalized.MaxScore);
        Assert.Equal(finalized.Score, repeated.Score);
        Assert.Equal(QuizAttemptStatus.Finalized, finalized.Status);
        Assert.Equal(10m, (await db.QuizAttemptsSet.SingleAsync(x => x.Id == attempt.Id)).Score);
        await Assert.ThrowsAsync<ApiException>(() => service.SyncAnswersAsync(attempt.Id, participant.Id, new([]), default));
        await Assert.ThrowsAsync<ApiException>(() => service.FinalizeAsync(attempt.Id, Guid.NewGuid(), new("other", DateTimeOffset.UtcNow), default));

        var hiddenSession = new ExamSession
        {
            ExamId = exam.Id,
            Exam = exam,
            RoomCode = "QUIZ02",
            Status = SessionStatus.InProgress,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
            SupervisionModeSnapshot = SupervisionMode.Standard,
            QuizResultPolicySnapshot = QuizResultPolicy.Hidden,
            ExamVersionSnapshot = exam.Version
        };
        var hiddenParticipant = new SessionParticipant
        {
            Session = hiddenSession,
            SessionId = hiddenSession.Id,
            StudentCode = "S2",
            DisplayName = "Hidden",
            DeviceId = "d2",
            MachineName = "m2",
            AppVersion = "1",
            Status = ParticipantStatus.Approved
        };
        db.AddRange(
            hiddenSession,
            hiddenParticipant,
            new ControlPolicy
            {
                SessionId = hiddenSession.Id,
                Version = 1,
                Status = PolicyApplyStatus.Applied
            },
            new DevicePolicyStatus
            {
                SessionId = hiddenSession.Id,
                ParticipantId = hiddenParticipant.Id,
                PolicyVersion = 1,
                Status = PolicyApplyStatus.Applied
            });
        await db.SaveChangesAsync();

        var hiddenAttempt = await service.StartOrGetAttemptAsync(
            hiddenSession.Id,
            hiddenParticipant.Id,
            default);
        var hiddenQuestion = hiddenAttempt.Questions[0];
        await service.SyncAnswersAsync(
            hiddenAttempt.Id,
            hiddenParticipant.Id,
            new([
                new(
                    hiddenQuestion.Id,
                    [hiddenQuestion.Choices[1].Id],
                    1,
                    DateTimeOffset.UtcNow)
            ]),
            default);
        var hiddenFinalized = await service.FinalizeAsync(
            hiddenAttempt.Id,
            hiddenParticipant.Id,
            new("hidden-final", DateTimeOffset.UtcNow),
            default);

        Assert.False(hiddenFinalized.ScoreVisible);
        Assert.Null(hiddenFinalized.Score);
        Assert.DoesNotContain("isCorrect", JsonSerializer.Serialize(hiddenFinalized), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5m, (await db.QuizAttemptsSet.SingleAsync(x => x.Id == hiddenAttempt.Id)).Score);
        var teacherAttempt = Assert.Single(await service.ListAttemptsForSessionAsync(hiddenSession.Id, default));
        Assert.Equal(5m, teacherAttempt.Score);
    }

    private static byte[] Docx(params string[] lines)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            foreach (var line in lines)
                writer.Write($"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(line)}</w:t></w:r></w:p>");
            writer.Write("</w:body></w:document>");
        }
        return output.ToArray();
    }
}
