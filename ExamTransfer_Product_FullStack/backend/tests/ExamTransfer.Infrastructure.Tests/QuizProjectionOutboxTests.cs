using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class QuizProjectionOutboxTests
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Projection_methods_preserve_queue_contract_order_and_web_json_payloads()
    {
        var createdAt = new DateTimeOffset(2026, 7, 31, 1, 2, 3, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(1);
        var examId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var questionId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var choiceId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var sessionId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        var participantId = Guid.Parse("50000000-0000-0000-0000-000000000005");
        var attemptId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        var answerId = Guid.Parse("70000000-0000-0000-0000-000000000007");
        var sourceId = Guid.Parse("80000000-0000-0000-0000-000000000008");
        var teacherId = Guid.Parse("90000000-0000-0000-0000-000000000009");
        var graderId = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
        var selectedChoiceIds = $"[\"{choiceId}\"]";
        var snapshotJson = "[{\"id\":\"snapshot\"}]";
        const string sourcePath = @"D:\quiz-source\source.docx";

        var question = new QuizQuestion
        {
            Id = questionId,
            ExamId = examId,
            Version = 7,
            Order = 2,
            Text = "Câu hỏi?",
            Points = 2.35m,
            Multiple = true,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
        var choice = new QuizChoice
        {
            Id = choiceId,
            QuestionId = questionId,
            Order = 3,
            Text = "Đáp án",
            IsCorrect = true,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
        var attempt = new QuizAttempt
        {
            Id = attemptId,
            SessionId = sessionId,
            ParticipantId = participantId,
            ExamVersion = 7,
            ResultPolicySnapshot = QuizResultPolicy.ShowAfterSubmission,
            Status = QuizAttemptStatus.Finalized,
            StartedAtUtc = createdAt.AddMinutes(-30),
            DeadlineUtc = createdAt,
            FinalizedAtUtc = createdAt.AddSeconds(-1),
            AutoScore = 8.75m,
            Score = null,
            MaxScore = 10.00m,
            GradingStatus = GradingStatus.Graded,
            GeneralComment = null,
            GraderId = graderId,
            GradedAtUtc = updatedAt,
            ReturnedAtUtc = null,
            SnapshotJson = snapshotJson,
            FinalizeIdempotencyKey = "final-key",
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
        var answer = new QuizAnswer
        {
            Id = answerId,
            AttemptId = attemptId,
            QuestionId = questionId,
            ChoiceIdsJson = selectedChoiceIds,
            Revision = 12,
            ClientUpdatedAtUtc = createdAt.AddSeconds(-5),
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
        var source = new QuizImportSource
        {
            Id = sourceId,
            ExamId = examId,
            ExamVersion = 7,
            OriginalName = "nguồn.docx",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            SizeBytes = 12345,
            Sha256 = new string('a', 64),
            RelativePath = "must-not-be-projected.docx",
            Status = "Committed",
            CreatedBy = teacherId,
            ImportedAtUtc = createdAt.AddMinutes(-2),
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
        var outbox = new CapturingOutbox();
        var projection = new QuizProjectionOutbox(outbox);

        await projection.EnqueueChoiceDeleteAsync(choiceId, default);
        await projection.EnqueueQuestionDeleteAsync(questionId, default);
        await projection.EnqueueQuestionUpsertAsync(question, default);
        await projection.EnqueueChoiceUpsertAsync(choice, default);
        await projection.EnqueueAttemptUpsertAsync(attempt, default);
        await projection.EnqueueAnswerUpsertAsync(answer, default);
        await projection.EnqueueSourceUpsertAsync(source, sourcePath, default);

        Assert.Collection(
            outbox.Calls,
            call => AssertCall(call, "quiz_choices", choiceId, "delete",
                new { id = choiceId }),
            call => AssertCall(call, "quiz_questions", questionId, "delete",
                new { id = questionId }),
            call => AssertCall(call, "quiz_questions", questionId, "upsert",
                new
                {
                    id = questionId,
                    exam_id = examId,
                    version = 7,
                    sort_order = 2,
                    question_text = "Câu hỏi?",
                    points = 2.35m,
                    multiple = true,
                    created_at = createdAt,
                    updated_at = updatedAt
                }),
            call => AssertCall(call, "quiz_choices", choiceId, "upsert",
                new
                {
                    id = choiceId,
                    question_id = questionId,
                    sort_order = 3,
                    choice_text = "Đáp án",
                    is_correct = true,
                    created_at = createdAt,
                    updated_at = updatedAt
                }),
            call => AssertCall(call, "quiz_attempts", attemptId, "upsert",
                new
                {
                    id = attemptId,
                    session_id = sessionId,
                    participant_id = participantId,
                    attempt_number = 1,
                    exam_version = 7,
                    result_policy = "ShowAfterSubmission",
                    status = "Finalized",
                    started_at = createdAt.AddMinutes(-30),
                    deadline_at = createdAt,
                    finalized_at = (DateTimeOffset?)createdAt.AddSeconds(-1),
                    auto_score = (decimal?)8.75m,
                    score = (decimal?)null,
                    max_score = 10.00m,
                    grading_status = "Graded",
                    general_comment = (string?)null,
                    grader_id = (Guid?)graderId,
                    graded_at = (DateTimeOffset?)updatedAt,
                    returned_at = (DateTimeOffset?)null,
                    snapshot_json = snapshotJson,
                    finalize_idempotency_key = "final-key",
                    created_at = createdAt,
                    updated_at = updatedAt
                }),
            call => AssertCall(call, "quiz_answers", answerId, "upsert",
                new
                {
                    id = answerId,
                    attempt_id = attemptId,
                    question_id = questionId,
                    choice_ids = selectedChoiceIds,
                    revision = 12L,
                    client_updated_at = createdAt.AddSeconds(-5),
                    created_at = createdAt,
                    updated_at = updatedAt
                }),
            call => AssertCall(call, "quiz_import_sources", sourceId, "upsert",
                new
                {
                    id = sourceId,
                    exam_id = examId,
                    exam_version = 7,
                    original_name = "nguồn.docx",
                    mime_type = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    size_bytes = 12345L,
                    sha256 = new string('a', 64),
                    status = "Committed",
                    created_by = teacherId,
                    imported_at = createdAt.AddMinutes(-2),
                    created_at = createdAt,
                    updated_at = updatedAt
                },
                sourcePath));
    }

    [Fact]
    public void Projection_outbox_has_only_the_existing_outbox_dependency()
    {
        var constructor = Assert.Single(typeof(QuizProjectionOutbox).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(IOutboxService), parameter.ParameterType);
        Assert.DoesNotContain(
            typeof(QuizProjectionOutbox).GetMethods(),
            method => method.Name.Contains("SaveChanges", StringComparison.Ordinal)
                || method.Name.Contains("Transaction", StringComparison.Ordinal)
                || method.Name.Contains("Push", StringComparison.Ordinal));
    }

    private static void AssertCall(
        OutboxCall call,
        string entityType,
        Guid entityId,
        string operation,
        object expectedPayload,
        string? filePath = null)
    {
        Assert.Equal(entityType, call.EntityType);
        Assert.Equal(entityId.ToString(), call.EntityId);
        Assert.Equal(operation, call.Operation);
        Assert.Equal(filePath, call.FilePath);
        Assert.Equal(
            JsonSerializer.Serialize(expectedPayload, Json),
            JsonSerializer.Serialize(call.Payload, Json));
    }

    private sealed class CapturingOutbox : IOutboxService
    {
        public List<OutboxCall> Calls { get; } = [];

        public Task EnqueueAsync(
            string entityType,
            string entityId,
            string operation,
            object payload,
            string? filePath = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(entityType, entityId, operation, payload, filePath));
            return Task.CompletedTask;
        }
    }

    private sealed record OutboxCall(
        string EntityType,
        string EntityId,
        string Operation,
        object Payload,
        string? FilePath);
}
