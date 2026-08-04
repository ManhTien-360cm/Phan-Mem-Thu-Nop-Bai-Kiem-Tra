using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class StudentNotificationAndResultContractTests
{
    private static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 8, 2, 3, 4, 5, TimeSpan.Zero);

    public static TheoryData<StudentNotificationEventType> NotificationEventTypes =>
        new()
        {
            StudentNotificationEventType.ParticipantApproved,
            StudentNotificationEventType.ParticipantAdmissionRejected,
            StudentNotificationEventType.TeacherMessageReceived,
            StudentNotificationEventType.SubmissionRejected,
            StudentNotificationEventType.ResubmitAllowed,
            StudentNotificationEventType.GradeReturned,
            StudentNotificationEventType.QuizGradeReturned,
            StudentNotificationEventType.GradeReopened,
            StudentNotificationEventType.QuizGradeReopened
        };

    [Theory]
    [MemberData(nameof(NotificationEventTypes))]
    public void Notification_AllSupportedEventTypesRoundTripAsProductionStrings(
        StudentNotificationEventType eventType)
    {
        var source = ValidNotification(eventType);

        var json = JsonSerializer.Serialize(source, ProductionJson());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(eventType.ToString(), document.RootElement.GetProperty("eventType").GetString());
        Assert.True(document.RootElement.TryGetProperty("eventId", out _));
        Assert.False(document.RootElement.TryGetProperty("EventId", out _));

        var roundTrip = JsonSerializer.Deserialize<StudentNotificationEventDto>(json, ProductionJson());
        Assert.NotNull(roundTrip);
        StudentNotificationEventValidator.EnsureValid(roundTrip);
        Assert.Equal(source, roundTrip);
    }

    [Fact]
    public void Notification_IdentifiersDecimalsUtcNullableFieldsAndRevisionRoundTrip()
    {
        var source = ValidNotification(StudentNotificationEventType.GradeReturned) with
        {
            AttemptId = null,
            Message = "Kết quả đã được trả.",
            Reason = null,
            Score = 7.123456789012345678m,
            MaxScore = 10.000000000000000001m,
            Revision = 42
        };

        var json = JsonSerializer.Serialize(source, ProductionJson());
        var roundTrip = JsonSerializer.Deserialize<StudentNotificationEventDto>(json, ProductionJson());

        Assert.NotNull(roundTrip);
        Assert.Equal(source.EventId, roundTrip.EventId);
        Assert.Equal(source.SessionId, roundTrip.SessionId);
        Assert.Equal(source.ParticipantId, roundTrip.ParticipantId);
        Assert.Equal(source.SubmissionId, roundTrip.SubmissionId);
        Assert.Null(roundTrip.AttemptId);
        Assert.Equal(source.Score, roundTrip.Score);
        Assert.Equal(source.MaxScore, roundTrip.MaxScore);
        Assert.Equal(TimeSpan.Zero, roundTrip.OccurredAtUtc.Offset);
        Assert.Equal(source.OccurredAtUtc, roundTrip.OccurredAtUtc);
        Assert.Equal(42, roundTrip.Revision);
    }

    [Fact]
    public void Notification_UnknownStringAndNumericEventTypesFailClosed()
    {
        const string unknownString = """
            {"eventId":"00000000-0000-0000-0000-000000000001","eventType":"GradePublished","sessionId":"00000000-0000-0000-0000-000000000002","occurredAtUtc":"2026-08-02T03:04:05Z","revision":0}
            """;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<StudentNotificationEventDto>(unknownString, ProductionJson()));

        var unknownNumeric = ValidNotification(StudentNotificationEventType.ParticipantApproved) with
        {
            EventType = (StudentNotificationEventType)999
        };
        Assert.Throws<ArgumentException>(() =>
            StudentNotificationEventValidator.EnsureValid(unknownNumeric));
    }

    [Fact]
    public void Notification_MissingRequiredJsonFieldIsRejected()
    {
        const string missingRevision = """
            {"eventId":"00000000-0000-0000-0000-000000000001","eventType":"TeacherMessageReceived","sessionId":"00000000-0000-0000-0000-000000000002","message":"Xin chào","occurredAtUtc":"2026-08-02T03:04:05Z"}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<StudentNotificationEventDto>(missingRevision, ProductionJson()));
    }

    [Fact]
    public void Notification_ParticipantApprovedWithoutParticipantIsRejected()
    {
        var value = ValidNotification(StudentNotificationEventType.ParticipantApproved) with
        {
            ParticipantId = null
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Fact]
    public void Notification_TeacherMessageWithoutMessageIsRejected()
    {
        var value = ValidNotification(StudentNotificationEventType.TeacherMessageReceived) with
        {
            Message = "  "
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Fact]
    public void Notification_GradeReturnedWithoutSubmissionIsRejected()
    {
        var value = ValidNotification(StudentNotificationEventType.GradeReturned) with
        {
            SubmissionId = null
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Fact]
    public void Notification_QuizGradeReturnedWithoutAttemptIsRejected()
    {
        var value = ValidNotification(StudentNotificationEventType.QuizGradeReturned) with
        {
            AttemptId = null
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Fact]
    public void Notification_GradeReopenedCannotUseAttemptIdentity()
    {
        var value = ValidNotification(StudentNotificationEventType.GradeReopened) with
        {
            SubmissionId = null,
            AttemptId = Guid.NewGuid()
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Fact]
    public void Notification_QuizGradeReopenedCannotUseSubmissionIdentity()
    {
        var value = ValidNotification(StudentNotificationEventType.QuizGradeReopened) with
        {
            AttemptId = null,
            SubmissionId = Guid.NewGuid()
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Theory]
    [InlineData(-0.01, 10)]
    [InlineData(11, 10)]
    public void Notification_InvalidScoreIsRejected(double score, double maxScore)
    {
        var value = ValidNotification(StudentNotificationEventType.GradeReturned) with
        {
            Score = (decimal)score,
            MaxScore = (decimal)maxScore
        };

        Assert.Throws<ArgumentException>(() => StudentNotificationEventValidator.EnsureValid(value));
    }

    [Fact]
    public void Notification_EmptyGuidAndNonUtcTimestampAreRejected()
    {
        var value = ValidNotification(StudentNotificationEventType.ParticipantApproved) with
        {
            EventId = Guid.Empty,
            OccurredAtUtc = OccurredAtUtc.ToOffset(TimeSpan.FromHours(7))
        };

        var errors = StudentNotificationEventValidator.Validate(value);
        Assert.Contains(errors, error => error.Contains(nameof(value.EventId), StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(nameof(value.OccurredAtUtc), StringComparison.Ordinal));
    }

    [Fact]
    public void Result_EssayFileRoundTripsWithSafeAttachments()
    {
        var source = ValidEssayResult();

        var json = JsonSerializer.Serialize(source, ProductionJson());
        var roundTrip = JsonSerializer.Deserialize<StudentResultDto>(json, ProductionJson());

        Assert.NotNull(roundTrip);
        StudentResultValidator.EnsureValid(roundTrip);
        Assert.Equal(source.ResultType, roundTrip.ResultType);
        Assert.Equal(source.ExamId, roundTrip.ExamId);
        Assert.Equal(source.ExamTitle, roundTrip.ExamTitle);
        Assert.Equal(source.SessionId, roundTrip.SessionId);
        Assert.Equal(source.SubmissionId, roundTrip.SubmissionId);
        Assert.Equal(source.AttemptId, roundTrip.AttemptId);
        Assert.Equal(source.AttemptNumber, roundTrip.AttemptNumber);
        Assert.Equal(source.Status, roundTrip.Status);
        Assert.Equal(source.Score, roundTrip.Score);
        Assert.Equal(source.MaxScore, roundTrip.MaxScore);
        Assert.Equal(source.GeneralComment, roundTrip.GeneralComment);
        Assert.Equal(source.ReturnedAtUtc, roundTrip.ReturnedAtUtc);
        Assert.Equal(source.Attachments.ToArray(), roundTrip.Attachments.ToArray());
        Assert.DoesNotContain("download", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Result_QuizRoundTripsWithSummaryAndEmptyAttachments()
    {
        var source = ValidQuizResult();

        var json = JsonSerializer.Serialize(source, ProductionJson());
        var roundTrip = JsonSerializer.Deserialize<StudentResultDto>(json, ProductionJson());

        Assert.NotNull(roundTrip);
        StudentResultValidator.EnsureValid(roundTrip);
        Assert.Equal(source.QuizSummary, roundTrip.QuizSummary);
        Assert.NotNull(roundTrip.Attachments);
        Assert.Empty(roundTrip.Attachments);
        Assert.Null(roundTrip.SubmissionId);
        Assert.Equal(source.AttemptId, roundTrip.AttemptId);
    }

    [Fact]
    public void Result_MissingAttachmentListDeserializesAsEmptyInsteadOfNull()
    {
        const string json = """
            {"resultType":"Quiz","examId":"00000000-0000-0000-0000-000000000001","examTitle":"Quiz","sessionId":"00000000-0000-0000-0000-000000000002","attemptId":"00000000-0000-0000-0000-000000000003","attemptNumber":1,"status":"Graded"}
            """;

        var result = JsonSerializer.Deserialize<StudentResultDto>(json, ProductionJson());

        Assert.NotNull(result);
        Assert.NotNull(result.Attachments);
        Assert.Empty(result.Attachments);
        StudentResultValidator.EnsureValid(result);
    }

    [Fact]
    public void Result_EssayFileRequiresSubmissionAndRejectsAttemptOrQuizSummary()
    {
        var value = ValidEssayResult() with
        {
            SubmissionId = null,
            AttemptId = Guid.NewGuid(),
            QuizSummary = ValidSummary()
        };

        Assert.Throws<ArgumentException>(() => StudentResultValidator.EnsureValid(value));
    }

    [Fact]
    public void Result_QuizRequiresAttemptAndNeverInfersEssayFromNullFields()
    {
        var value = ValidQuizResult() with { AttemptId = null };

        var errors = StudentResultValidator.Validate(value);
        Assert.Contains(errors, error => error.Contains(nameof(value.AttemptId), StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("EssayFile", StringComparison.Ordinal));
    }

    [Fact]
    public void Result_QuizRejectsSubmissionIdentity()
    {
        var value = ValidQuizResult() with { SubmissionId = Guid.NewGuid() };

        Assert.Throws<ArgumentException>(() => StudentResultValidator.EnsureValid(value));
    }

    [Fact]
    public void Result_GradedRejectsReturnedTimestamp()
    {
        var value = ValidQuizResult() with
        {
            Status = StudentResultStatus.Graded,
            ReturnedAtUtc = OccurredAtUtc
        };

        Assert.Throws<ArgumentException>(() => StudentResultValidator.EnsureValid(value));
    }

    [Fact]
    public void Result_ReturnedRequiresReturnedTimestamp()
    {
        var value = ValidEssayResult() with { ReturnedAtUtc = null };

        Assert.Throws<ArgumentException>(() => StudentResultValidator.EnsureValid(value));
    }

    [Fact]
    public void Result_UnknownTypeAndStatusFailClosed()
    {
        const string unknownType = """
            {"resultType":"Worksheet","examId":"00000000-0000-0000-0000-000000000001","examTitle":"Exam","sessionId":"00000000-0000-0000-0000-000000000002","attemptNumber":1,"status":"Graded"}
            """;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<StudentResultDto>(unknownType, ProductionJson()));

        var unknownNumeric = ValidQuizResult() with
        {
            ResultType = (StudentResultType)999,
            Status = (StudentResultStatus)999
        };
        Assert.Throws<ArgumentException>(() => StudentResultValidator.EnsureValid(unknownNumeric));
    }

    [Fact]
    public void Result_UnsafeAttachmentPathAndInvalidSummaryAreRejected()
    {
        var value = ValidQuizResult() with
        {
            Attachments =
            [
                new StudentResultAttachmentDto
                {
                    AttachmentId = Guid.NewGuid(),
                    FileName = @"C:\private\graded.docx",
                    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    SizeBytes = 100
                }
            ],
            QuizSummary = ValidSummary() with { CorrectCount = 5, IncorrectCount = 5 }
        };

        var errors = StudentResultValidator.Validate(value);
        Assert.Contains(errors, error => error.Contains("metadata, not a path", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("correct and incorrect", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingRealtimeAndGradeDtosStillUseTheirOriginalJsonContracts()
    {
        var submissionId = Guid.NewGuid();
        var existingGrade = new GradeDto(
            submissionId,
            GradingStatus.Returned,
            8.25m,
            10m,
            [],
            "Tốt",
            [new FileDescriptorDto(Guid.NewGuid(), "graded.docx", 321, "abc", "application/docx", "/api/download")],
            OccurredAtUtc,
            "row-version");
        var existingEnvelope = new RealtimeEnvelope<GradeReturnedEvent>(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            OccurredAtUtc,
            RealtimeEvents.GradeReturned,
            new GradeReturnedEvent(submissionId, 8.25m, 10m));

        var gradeJson = JsonSerializer.Serialize(existingGrade, ProductionJson());
        var envelopeJson = JsonSerializer.Serialize(existingEnvelope, ProductionJson());

        var gradeRoundTrip = JsonSerializer.Deserialize<GradeDto>(gradeJson, ProductionJson());
        Assert.NotNull(gradeRoundTrip);
        Assert.Equal(existingGrade.SubmissionId, gradeRoundTrip.SubmissionId);
        Assert.Equal(existingGrade.Status, gradeRoundTrip.Status);
        Assert.Equal(existingGrade.Score, gradeRoundTrip.Score);
        Assert.Equal(existingGrade.MaxScore, gradeRoundTrip.MaxScore);
        Assert.Equal(existingGrade.RubricScores.ToArray(), gradeRoundTrip.RubricScores.ToArray());
        Assert.Equal(existingGrade.GeneralComment, gradeRoundTrip.GeneralComment);
        Assert.Equal(existingGrade.Attachments.ToArray(), gradeRoundTrip.Attachments.ToArray());
        Assert.Equal(existingGrade.ReturnedAtUtc, gradeRoundTrip.ReturnedAtUtc);
        Assert.Equal(existingGrade.RowVersion, gradeRoundTrip.RowVersion);
        Assert.Equal(existingEnvelope, JsonSerializer.Deserialize<RealtimeEnvelope<GradeReturnedEvent>>(
            envelopeJson,
            ProductionJson()));
    }

    private static StudentNotificationEventDto ValidNotification(StudentNotificationEventType eventType)
    {
        var value = new StudentNotificationEventDto
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            SessionId = Guid.NewGuid(),
            OccurredAtUtc = OccurredAtUtc,
            Revision = 0
        };

        return eventType switch
        {
            StudentNotificationEventType.ParticipantApproved or
            StudentNotificationEventType.ParticipantAdmissionRejected =>
                value with { ParticipantId = Guid.NewGuid(), Reason = "Yêu cầu không được chấp nhận." },
            StudentNotificationEventType.TeacherMessageReceived =>
                value with { Message = "Giáo viên đã gửi thông báo." },
            StudentNotificationEventType.SubmissionRejected or
            StudentNotificationEventType.ResubmitAllowed or
            StudentNotificationEventType.GradeReturned or
            StudentNotificationEventType.GradeReopened =>
                value with { ParticipantId = Guid.NewGuid(), SubmissionId = Guid.NewGuid() },
            StudentNotificationEventType.QuizGradeReturned or
            StudentNotificationEventType.QuizGradeReopened =>
                value with { ParticipantId = Guid.NewGuid(), AttemptId = Guid.NewGuid() },
            _ => value
        };
    }

    private static StudentResultDto ValidEssayResult() => new()
    {
        ResultType = StudentResultType.EssayFile,
        ExamId = Guid.NewGuid(),
        ExamTitle = "Bài tự luận",
        SessionId = Guid.NewGuid(),
        SubmissionId = Guid.NewGuid(),
        AttemptNumber = 1,
        Status = StudentResultStatus.Returned,
        Score = 7.5m,
        MaxScore = 10m,
        GeneralComment = "Lập luận rõ ràng.",
        ReturnedAtUtc = OccurredAtUtc,
        Attachments =
        [
            new StudentResultAttachmentDto
            {
                AttachmentId = Guid.NewGuid(),
                FileName = "feedback.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1234
            }
        ]
    };

    private static StudentResultDto ValidQuizResult() => new()
    {
        ResultType = StudentResultType.Quiz,
        ExamId = Guid.NewGuid(),
        ExamTitle = "Bài trắc nghiệm",
        SessionId = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(),
        AttemptNumber = 2,
        Status = StudentResultStatus.Returned,
        Score = 8m,
        MaxScore = 10m,
        GeneralComment = "Hoàn thành tốt.",
        ReturnedAtUtc = OccurredAtUtc,
        Attachments = [],
        QuizSummary = ValidSummary()
    };

    private static StudentQuizResultSummaryDto ValidSummary() => new()
    {
        TotalQuestions = 10,
        AnsweredQuestions = 9,
        CorrectCount = 8,
        IncorrectCount = 1,
        UnansweredCount = 1,
        EarnedPoints = 8m,
        MaxPoints = 10m
    };

    private static JsonSerializerOptions ProductionJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
