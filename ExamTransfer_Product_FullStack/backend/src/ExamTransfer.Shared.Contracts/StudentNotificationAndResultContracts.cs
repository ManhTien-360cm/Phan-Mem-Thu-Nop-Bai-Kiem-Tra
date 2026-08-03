using System.Text.Json.Serialization;

namespace ExamTransfer.Shared.Contracts;

public enum StudentNotificationEventType
{
    ParticipantApproved = 1,
    ParticipantAdmissionRejected,
    TeacherMessageReceived,
    SubmissionRejected,
    ResubmitAllowed,
    GradeReturned,
    QuizGradeReturned,
    GradeReopened,
    QuizGradeReopened
}

public sealed record StudentNotificationEventDto
{
    [JsonRequired]
    public Guid EventId { get; init; }

    [JsonRequired]
    public StudentNotificationEventType EventType { get; init; }

    [JsonRequired]
    public Guid SessionId { get; init; }

    public Guid? ParticipantId { get; init; }
    public Guid? SubmissionId { get; init; }
    public Guid? AttemptId { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public decimal? Score { get; init; }
    public decimal? MaxScore { get; init; }

    [JsonRequired]
    public DateTimeOffset OccurredAtUtc { get; init; }

    [JsonRequired]
    public long Revision { get; init; }
}

public static class StudentNotificationEventValidator
{
    public static IReadOnlyList<string> Validate(StudentNotificationEventDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<string>();

        RequireNonEmpty(value.EventId, nameof(value.EventId), errors);
        RequireNonEmpty(value.SessionId, nameof(value.SessionId), errors);
        RejectEmpty(value.ParticipantId, nameof(value.ParticipantId), errors);
        RejectEmpty(value.SubmissionId, nameof(value.SubmissionId), errors);
        RejectEmpty(value.AttemptId, nameof(value.AttemptId), errors);

        if (!Enum.IsDefined(value.EventType))
            errors.Add($"{nameof(value.EventType)} is not supported.");
        if (value.OccurredAtUtc == default || value.OccurredAtUtc.Offset != TimeSpan.Zero)
            errors.Add($"{nameof(value.OccurredAtUtc)} must be a non-default UTC timestamp.");
        if (value.Revision < 0)
            errors.Add($"{nameof(value.Revision)} must be greater than or equal to zero.");
        if (value.Score < 0)
            errors.Add($"{nameof(value.Score)} must be greater than or equal to zero when present.");
        if (value.MaxScore <= 0)
            errors.Add($"{nameof(value.MaxScore)} must be greater than zero when present.");
        if (value.Score.HasValue && value.MaxScore.HasValue && value.Score > value.MaxScore)
            errors.Add($"{nameof(value.Score)} cannot exceed {nameof(value.MaxScore)}.");
        if (value.Message is not null && string.IsNullOrWhiteSpace(value.Message))
            errors.Add($"{nameof(value.Message)} cannot be empty or whitespace when present.");
        if (value.Reason is not null && string.IsNullOrWhiteSpace(value.Reason))
            errors.Add($"{nameof(value.Reason)} cannot be empty or whitespace when present.");

        switch (value.EventType)
        {
            case StudentNotificationEventType.ParticipantApproved:
            case StudentNotificationEventType.ParticipantAdmissionRejected:
                RequireNonEmpty(value.ParticipantId, nameof(value.ParticipantId), errors);
                break;

            case StudentNotificationEventType.TeacherMessageReceived:
                if (string.IsNullOrWhiteSpace(value.Message))
                    errors.Add($"{nameof(value.Message)} is required for {value.EventType}.");
                break;

            case StudentNotificationEventType.SubmissionRejected:
            case StudentNotificationEventType.ResubmitAllowed:
            case StudentNotificationEventType.GradeReturned:
            case StudentNotificationEventType.GradeReopened:
                RequireNonEmpty(value.ParticipantId, nameof(value.ParticipantId), errors);
                RequireNonEmpty(value.SubmissionId, nameof(value.SubmissionId), errors);
                RejectPresent(value.AttemptId, nameof(value.AttemptId), value.EventType, errors);
                break;

            case StudentNotificationEventType.QuizGradeReturned:
            case StudentNotificationEventType.QuizGradeReopened:
                RequireNonEmpty(value.ParticipantId, nameof(value.ParticipantId), errors);
                RequireNonEmpty(value.AttemptId, nameof(value.AttemptId), errors);
                RejectPresent(value.SubmissionId, nameof(value.SubmissionId), value.EventType, errors);
                break;
        }

        return errors;
    }

    public static void EnsureValid(StudentNotificationEventDto value)
    {
        var errors = Validate(value);
        if (errors.Count > 0)
            throw new ArgumentException(
                $"Student notification contract is invalid: {string.Join(" ", errors)}",
                nameof(value));
    }

    private static void RequireNonEmpty(Guid value, string field, ICollection<string> errors)
    {
        if (value == Guid.Empty)
            errors.Add($"{field} is required and cannot be an empty GUID.");
    }

    private static void RequireNonEmpty(Guid? value, string field, ICollection<string> errors)
    {
        if (!value.HasValue || value.Value == Guid.Empty)
            errors.Add($"{field} is required and cannot be an empty GUID.");
    }

    private static void RejectEmpty(Guid? value, string field, ICollection<string> errors)
    {
        if (value == Guid.Empty)
            errors.Add($"{field} cannot be an empty GUID when present.");
    }

    private static void RejectPresent(
        Guid? value,
        string field,
        StudentNotificationEventType eventType,
        ICollection<string> errors)
    {
        if (value.HasValue)
            errors.Add($"{field} is not valid for {eventType}.");
    }
}

public enum StudentResultType
{
    EssayFile = 1,
    Quiz
}

public enum StudentResultStatus
{
    Graded = 1,
    Returned
}

public sealed record StudentResultAttachmentDto
{
    public Guid AttachmentId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

public sealed record StudentQuizResultSummaryDto
{
    public int TotalQuestions { get; init; }
    public int AnsweredQuestions { get; init; }
    public int CorrectCount { get; init; }
    public int IncorrectCount { get; init; }
    public int UnansweredCount { get; init; }
    public decimal EarnedPoints { get; init; }
    public decimal MaxPoints { get; init; }
}

public sealed record StudentResultDto
{
    [JsonRequired]
    public StudentResultType ResultType { get; init; }

    [JsonRequired]
    public Guid ExamId { get; init; }

    [JsonRequired]
    public string ExamTitle { get; init; } = string.Empty;

    [JsonRequired]
    public Guid SessionId { get; init; }

    public Guid? SubmissionId { get; init; }
    public Guid? AttemptId { get; init; }

    [JsonRequired]
    public int AttemptNumber { get; init; }

    [JsonRequired]
    public StudentResultStatus Status { get; init; }

    public decimal? Score { get; init; }
    public decimal? MaxScore { get; init; }
    public string? GeneralComment { get; init; }
    public DateTimeOffset? ReturnedAtUtc { get; init; }
    public IReadOnlyList<StudentResultAttachmentDto> Attachments { get; init; } = [];
    public StudentQuizResultSummaryDto? QuizSummary { get; init; }
}

public sealed record StudentResultCursorDto
{
    [JsonRequired]
    public DateTimeOffset ReturnedAtUtc { get; init; }

    [JsonRequired]
    public StudentResultType ResultType { get; init; }

    [JsonRequired]
    public Guid ResultId { get; init; }
}

public sealed record StudentResultPageDto
{
    public IReadOnlyList<StudentResultDto> Items { get; init; } = [];
    public StudentResultCursorDto? NextCursor { get; init; }
}

public static class StudentResultValidator
{
    public static IReadOnlyList<string> Validate(StudentResultDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<string>();

        if (!Enum.IsDefined(value.ResultType))
            errors.Add($"{nameof(value.ResultType)} is not supported.");
        if (!Enum.IsDefined(value.Status))
            errors.Add($"{nameof(value.Status)} is not supported.");
        RequireNonEmpty(value.ExamId, nameof(value.ExamId), errors);
        RequireNonEmpty(value.SessionId, nameof(value.SessionId), errors);
        RejectEmpty(value.SubmissionId, nameof(value.SubmissionId), errors);
        RejectEmpty(value.AttemptId, nameof(value.AttemptId), errors);
        if (string.IsNullOrWhiteSpace(value.ExamTitle))
            errors.Add($"{nameof(value.ExamTitle)} is required and cannot be whitespace.");
        if (value.AttemptNumber <= 0)
            errors.Add($"{nameof(value.AttemptNumber)} must be greater than zero.");
        if (value.Score < 0)
            errors.Add($"{nameof(value.Score)} must be greater than or equal to zero when present.");
        if (value.MaxScore <= 0)
            errors.Add($"{nameof(value.MaxScore)} must be greater than zero when present.");
        if (value.Score.HasValue && value.MaxScore.HasValue && value.Score > value.MaxScore)
            errors.Add($"{nameof(value.Score)} cannot exceed {nameof(value.MaxScore)}.");
        if (value.ReturnedAtUtc.HasValue &&
            (value.ReturnedAtUtc.Value == default || value.ReturnedAtUtc.Value.Offset != TimeSpan.Zero))
        {
            errors.Add($"{nameof(value.ReturnedAtUtc)} must be UTC when present.");
        }
        if (value.GeneralComment is not null && string.IsNullOrWhiteSpace(value.GeneralComment))
            errors.Add($"{nameof(value.GeneralComment)} cannot be empty or whitespace when present.");

        switch (value.Status)
        {
            case StudentResultStatus.Graded when value.ReturnedAtUtc.HasValue:
                errors.Add($"{nameof(value.ReturnedAtUtc)} must be null while status is Graded.");
                break;
            case StudentResultStatus.Returned when !value.ReturnedAtUtc.HasValue:
                errors.Add($"{nameof(value.ReturnedAtUtc)} is required while status is Returned.");
                break;
        }

        switch (value.ResultType)
        {
            case StudentResultType.EssayFile:
                RequireNonEmpty(value.SubmissionId, nameof(value.SubmissionId), errors);
                if (value.AttemptId.HasValue)
                    errors.Add($"{nameof(value.AttemptId)} must be null for EssayFile results.");
                if (value.QuizSummary is not null)
                    errors.Add($"{nameof(value.QuizSummary)} must be null for EssayFile results.");
                break;

            case StudentResultType.Quiz:
                RequireNonEmpty(value.AttemptId, nameof(value.AttemptId), errors);
                if (value.SubmissionId.HasValue)
                    errors.Add($"{nameof(value.SubmissionId)} must be null for Quiz results.");
                break;
        }

        ValidateAttachments(value.Attachments, errors);
        if (value.QuizSummary is not null)
            ValidateQuizSummary(value.QuizSummary, errors);

        return errors;
    }

    public static void EnsureValid(StudentResultDto value)
    {
        var errors = Validate(value);
        if (errors.Count > 0)
            throw new ArgumentException(
                $"Student result contract is invalid: {string.Join(" ", errors)}",
                nameof(value));
    }

    private static void ValidateAttachments(
        IReadOnlyList<StudentResultAttachmentDto>? attachments,
        ICollection<string> errors)
    {
        if (attachments is null)
        {
            errors.Add($"{nameof(StudentResultDto.Attachments)} cannot be null.");
            return;
        }

        foreach (var attachment in attachments)
        {
            if (attachment is null)
            {
                errors.Add($"{nameof(StudentResultDto.Attachments)} cannot contain null entries.");
                continue;
            }
            RequireNonEmpty(attachment.AttachmentId, nameof(attachment.AttachmentId), errors);
            if (string.IsNullOrWhiteSpace(attachment.FileName))
                errors.Add($"{nameof(attachment.FileName)} is required.");
            else if (attachment.FileName.Contains('/') || attachment.FileName.Contains('\\') ||
                     Path.IsPathRooted(attachment.FileName))
                errors.Add($"{nameof(attachment.FileName)} must be metadata, not a path.");
            if (string.IsNullOrWhiteSpace(attachment.ContentType))
                errors.Add($"{nameof(attachment.ContentType)} is required.");
            if (attachment.SizeBytes < 0)
                errors.Add($"{nameof(attachment.SizeBytes)} must be greater than or equal to zero.");
        }
    }

    private static void ValidateQuizSummary(
        StudentQuizResultSummaryDto summary,
        ICollection<string> errors)
    {
        if (summary.TotalQuestions < 0 || summary.AnsweredQuestions < 0 ||
            summary.CorrectCount < 0 || summary.IncorrectCount < 0 ||
            summary.UnansweredCount < 0)
        {
            errors.Add("Quiz summary counts must be greater than or equal to zero.");
        }
        if (summary.AnsweredQuestions + summary.UnansweredCount != summary.TotalQuestions)
            errors.Add("Quiz summary answered and unanswered counts must equal the total question count.");
        if (summary.CorrectCount + summary.IncorrectCount != summary.AnsweredQuestions)
            errors.Add("Quiz summary correct and incorrect counts must equal the answered question count.");
        if (summary.EarnedPoints < 0)
            errors.Add($"{nameof(summary.EarnedPoints)} must be greater than or equal to zero.");
        if (summary.MaxPoints <= 0)
            errors.Add($"{nameof(summary.MaxPoints)} must be greater than zero.");
        if (summary.EarnedPoints > summary.MaxPoints)
            errors.Add($"{nameof(summary.EarnedPoints)} cannot exceed {nameof(summary.MaxPoints)}.");
    }

    private static void RequireNonEmpty(Guid value, string field, ICollection<string> errors)
    {
        if (value == Guid.Empty)
            errors.Add($"{field} is required and cannot be an empty GUID.");
    }

    private static void RequireNonEmpty(Guid? value, string field, ICollection<string> errors)
    {
        if (!value.HasValue || value.Value == Guid.Empty)
            errors.Add($"{field} is required and cannot be an empty GUID.");
    }

    private static void RejectEmpty(Guid? value, string field, ICollection<string> errors)
    {
        if (value == Guid.Empty)
            errors.Add($"{field} cannot be an empty GUID when present.");
    }
}

public static class StudentResultPageValidator
{
    public const int MaxPageSize = 100;

    public static void EnsureValid(StudentResultPageDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Items is null || value.Items.Count > MaxPageSize)
            throw new ArgumentException("Student result page items are invalid.", nameof(value));
        foreach (var item in value.Items)
        {
            StudentResultValidator.EnsureValid(item);
            if (item.Status != StudentResultStatus.Returned)
                throw new ArgumentException("Student result pages may contain only Returned results.", nameof(value));
        }
        if (value.NextCursor is { } cursor)
        {
            if (cursor.ReturnedAtUtc == default || cursor.ReturnedAtUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException("Student result cursor timestamp must be non-default UTC.", nameof(value));
            if (!Enum.IsDefined(cursor.ResultType) || cursor.ResultId == Guid.Empty)
                throw new ArgumentException("Student result cursor identity is invalid.", nameof(value));
        }
    }
}
