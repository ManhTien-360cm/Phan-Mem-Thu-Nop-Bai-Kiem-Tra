using System.Data;
using System.Globalization;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class StudentResultService(AppDbContext db) : IStudentResultService
{
    public async Task<StudentResultPageDto> GetReturnedAsync(
        Guid actorId,
        string? actorOrganizationId,
        int pageSize,
        StudentResultCursorDto? cursor,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > StudentResultPageValidator.MaxPageSize)
            throw new ApiException(ErrorCodes.ValidationFailed, "Page size phải từ 1 đến 100.");
        ValidateCursor(cursor);

        var actor = await db.UsersSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actorId, cancellationToken);
        if (actor is null
            || !actor.IsActive
            || actor.Role != UserRole.Student
            || string.IsNullOrWhiteSpace(actorOrganizationId)
            || string.IsNullOrWhiteSpace(actor.OrganizationId)
            || !string.Equals(actor.OrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "Tài khoản học sinh hoặc tổ chức không hợp lệ.", 403);
        }

        var candidates = await ReadCandidatesAsync(
            actor.Id,
            actor.OrganizationId,
            pageSize + 1,
            cursor,
            cancellationToken);
        var hasMore = candidates.Count > pageSize;
        var pageCandidates = candidates.Take(pageSize).ToArray();

        var essayIds = pageCandidates
            .Where(x => x.ResultType == StudentResultType.EssayFile)
            .Select(x => x.ResultId)
            .ToArray();
        var quizIds = pageCandidates
            .Where(x => x.ResultType == StudentResultType.Quiz)
            .Select(x => x.ResultId)
            .ToArray();

        var essays = essayIds.Length == 0
            ? []
            : await db.GradesSet.AsNoTracking()
                .Include(x => x.Attachments)
                .Include(x => x.Submission).ThenInclude(x => x.Participant)
                .Include(x => x.Submission).ThenInclude(x => x.Session).ThenInclude(x => x.Exam)
                .Where(x => essayIds.Contains(x.SubmissionId))
                .ToListAsync(cancellationToken);
        var quizzes = quizIds.Length == 0
            ? []
            : await db.QuizAttemptsSet.AsNoTracking()
                .Include(x => x.Answers)
                .Include(x => x.Participant)
                .Include(x => x.Session).ThenInclude(x => x.Exam)
                .Where(x => quizIds.Contains(x.Id))
                .ToListAsync(cancellationToken);

        var essayById = essays.ToDictionary(x => x.SubmissionId);
        var quizById = quizzes.ToDictionary(x => x.Id);
        var items = new List<StudentResultDto>(pageCandidates.Length);
        foreach (var candidate in pageCandidates)
        {
            StudentResultDto result;
            if (candidate.ResultType == StudentResultType.EssayFile)
            {
                if (!essayById.TryGetValue(candidate.ResultId, out var grade))
                    throw Integrity("Returned EssayFile result disappeared while being read.");
                result = MapEssay(grade, actor);
            }
            else
            {
                if (!quizById.TryGetValue(candidate.ResultId, out var attempt))
                    throw Integrity("Returned Quiz result disappeared while being read.");
                result = await MapQuizAsync(attempt, actor, cancellationToken);
            }
            StudentResultValidator.EnsureValid(result);
            items.Add(result);
        }

        var last = hasMore ? pageCandidates[^1] : null;
        var page = new StudentResultPageDto
        {
            Items = items,
            NextCursor = last is null
                ? null
                : new StudentResultCursorDto
                {
                    ReturnedAtUtc = last.ReturnedAtUtc.ToUniversalTime(),
                    ResultType = last.ResultType,
                    ResultId = last.ResultId
                }
        };
        StudentResultPageValidator.EnsureValid(page);
        return page;
    }

    private async Task<List<ResultCandidate>> ReadCandidatesAsync(
        Guid actorId,
        string actorOrganizationId,
        int limit,
        StudentResultCursorDto? cursor,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "ResultType", "ResultId", "ReturnedAtUtc"
            FROM (
                SELECT 1 AS "ResultType", submission."Id" AS "ResultId", grade."ReturnedAtUtc" AS "ReturnedAtUtc"
                FROM "grades" grade
                JOIN "submissions" submission ON submission."Id" = grade."SubmissionId"
                JOIN "session_participants" participant
                  ON participant."Id" = submission."ParticipantId"
                 AND participant."SessionId" = submission."SessionId"
                JOIN "exam_sessions" session ON session."Id" = submission."SessionId"
                JOIN "exams" exam ON exam."Id" = session."ExamId"
                JOIN "users" owner
                  ON owner."Id" = exam."CreatedBy"
                 AND owner."IsActive" = 1
                 AND owner."OrganizationId" = @organizationId
                WHERE participant."UserId" = @actorId
                  AND participant."Status" = @approved
                  AND session."AccessMode" = @lanOnly
                  AND session."DeliveryTypeSnapshot" = @fileSubmission
                  AND exam."DeliveryType" = @fileSubmission
                  AND submission."SourceMode" <> 'PublicCloud' COLLATE NOCASE
                  AND grade."SourceMode" <> 'PublicCloud' COLLATE NOCASE
                  AND submission."IsOfficial" = 1
                  AND submission."Status" IN (@submitted, @lateSubmitted)
                  AND grade."Status" = @returned
                  AND grade."ReturnedAtUtc" IS NOT NULL
                UNION ALL
                SELECT 2 AS "ResultType", attempt."Id" AS "ResultId", attempt."ReturnedAtUtc" AS "ReturnedAtUtc"
                FROM "quiz_attempts" attempt
                JOIN "session_participants" participant
                  ON participant."Id" = attempt."ParticipantId"
                 AND participant."SessionId" = attempt."SessionId"
                JOIN "exam_sessions" session ON session."Id" = attempt."SessionId"
                JOIN "exams" exam ON exam."Id" = session."ExamId"
                JOIN "users" owner
                  ON owner."Id" = exam."CreatedBy"
                 AND owner."IsActive" = 1
                 AND owner."OrganizationId" = @organizationId
                WHERE participant."UserId" = @actorId
                  AND participant."Status" = @approved
                  AND session."AccessMode" = @lanOnly
                  AND session."DeliveryTypeSnapshot" = @multipleChoice
                  AND exam."DeliveryType" = @multipleChoice
                  AND attempt."SourceMode" <> 'PublicCloud' COLLATE NOCASE
                  AND attempt."Status" = @finalized
                  AND attempt."GradingStatus" = @returned
                  AND attempt."ReturnedAtUtc" IS NOT NULL
            ) result
            WHERE @cursorReturnedAtUtc IS NULL
               OR "ReturnedAtUtc" < @cursorReturnedAtUtc
               OR ("ReturnedAtUtc" = @cursorReturnedAtUtc AND "ResultType" > @cursorResultType)
               OR ("ReturnedAtUtc" = @cursorReturnedAtUtc AND "ResultType" = @cursorResultType
                   AND "ResultId" COLLATE NOCASE > @cursorResultId COLLATE NOCASE)
            ORDER BY "ReturnedAtUtc" DESC, "ResultType", "ResultId" COLLATE NOCASE
            LIMIT @limit;
            """;

        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@actorId", actorId);
            AddParameter(command, "@organizationId", actorOrganizationId);
            AddParameter(command, "@approved", (int)ParticipantStatus.Approved);
            AddParameter(command, "@lanOnly", (int)SessionAccessMode.LanOnly);
            AddParameter(command, "@fileSubmission", (int)ExamDeliveryType.FileSubmission);
            AddParameter(command, "@multipleChoice", (int)ExamDeliveryType.MultipleChoice);
            AddParameter(command, "@submitted", (int)SubmissionStatus.Submitted);
            AddParameter(command, "@lateSubmitted", (int)SubmissionStatus.LateSubmitted);
            AddParameter(command, "@finalized", (int)QuizAttemptStatus.Finalized);
            AddParameter(command, "@returned", (int)GradingStatus.Returned);
            AddParameter(command, "@cursorReturnedAtUtc", cursor?.ReturnedAtUtc);
            AddParameter(command, "@cursorResultType", cursor is null ? null : (int)cursor.ResultType);
            AddParameter(command, "@cursorResultId", cursor?.ResultId);
            AddParameter(command, "@limit", limit);

            var result = new List<ResultCandidate>(limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var resultType = (StudentResultType)Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                var resultId = Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!);
                var returnedAt = DateTimeOffset.Parse(
                    Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal).ToUniversalTime();
                result.Add(new(resultType, resultId, returnedAt));
            }
            return result;
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }

    private static StudentResultDto MapEssay(Grade grade, User actor)
    {
        var submission = grade.Submission;
        if (grade.Status != GradingStatus.Returned
            || !grade.ReturnedAtUtc.HasValue
            || submission.Participant.UserId != actor.Id
            || submission.Participant.Status != ParticipantStatus.Approved
            || submission.ParticipantId != submission.Participant.Id
            || submission.Participant.SessionId != submission.SessionId
            || submission.Session.ExamId != submission.Session.Exam.Id
            || submission.Session.AccessMode != SessionAccessMode.LanOnly
            || submission.Session.DeliveryTypeSnapshot != ExamDeliveryType.FileSubmission
            || submission.Session.Exam.DeliveryType != ExamDeliveryType.FileSubmission
            || string.Equals(submission.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)
            || string.Equals(grade.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase)
            || !submission.IsOfficial
            || !SubmissionStatePolicy.IsCompletedSubmissionStatus(submission.Status))
        {
            throw Integrity("EssayFile result failed its ownership or authoritative-state invariant.");
        }

        return new StudentResultDto
        {
            ResultType = StudentResultType.EssayFile,
            ExamId = submission.Session.ExamId,
            ExamTitle = submission.Session.Exam.Title,
            SessionId = submission.SessionId,
            SubmissionId = submission.Id,
            AttemptId = null,
            AttemptNumber = submission.AttemptNumber,
            Status = StudentResultStatus.Returned,
            Score = grade.Score,
            MaxScore = grade.MaxScore,
            GeneralComment = grade.GeneralComment,
            ReturnedAtUtc = grade.ReturnedAtUtc.Value.ToUniversalTime(),
            Attachments = grade.Attachments
                .OrderBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => new StudentResultAttachmentDto
                {
                    AttachmentId = x.Id,
                    FileName = SafeFileName(x.OriginalName, x.Id),
                    ContentType = string.IsNullOrWhiteSpace(x.MimeType)
                        ? "application/octet-stream"
                        : x.MimeType,
                    SizeBytes = x.SizeBytes
                })
                .ToArray(),
            QuizSummary = null
        };
    }

    private async Task<StudentResultDto> MapQuizAsync(
        QuizAttempt attempt,
        User actor,
        CancellationToken cancellationToken)
    {
        if (attempt.Status != QuizAttemptStatus.Finalized
            || attempt.GradingStatus != GradingStatus.Returned
            || !attempt.ReturnedAtUtc.HasValue
            || attempt.Score is null
            || attempt.AttemptNumber <= 0
            || attempt.Participant.UserId != actor.Id
            || attempt.Participant.Status != ParticipantStatus.Approved
            || attempt.ParticipantId != attempt.Participant.Id
            || attempt.Participant.SessionId != attempt.SessionId
            || attempt.Session.ExamId != attempt.Session.Exam.Id
            || attempt.Session.AccessMode != SessionAccessMode.LanOnly
            || attempt.Session.DeliveryTypeSnapshot != ExamDeliveryType.MultipleChoice
            || attempt.Session.Exam.DeliveryType != ExamDeliveryType.MultipleChoice
            || string.Equals(attempt.SourceMode, "PublicCloud", StringComparison.OrdinalIgnoreCase))
        {
            throw Integrity("Quiz result failed its ownership or authoritative-state invariant.");
        }

        var summary = await QuizGradeAuthoritativeScoring.CalculateAsync(db, attempt, cancellationToken);
        if (attempt.Score != summary.Score || attempt.MaxScore != summary.MaxScore)
            throw Integrity("Persisted Quiz score does not match the authoritative answer graph.");

        return new StudentResultDto
        {
            ResultType = StudentResultType.Quiz,
            ExamId = attempt.Session.ExamId,
            ExamTitle = attempt.Session.Exam.Title,
            SessionId = attempt.SessionId,
            SubmissionId = null,
            AttemptId = attempt.Id,
            AttemptNumber = attempt.AttemptNumber,
            Status = StudentResultStatus.Returned,
            Score = attempt.Score,
            MaxScore = attempt.MaxScore,
            GeneralComment = attempt.GeneralComment,
            ReturnedAtUtc = attempt.ReturnedAtUtc.Value.ToUniversalTime(),
            Attachments = [],
            QuizSummary = new StudentQuizResultSummaryDto
            {
                TotalQuestions = summary.TotalQuestions,
                AnsweredQuestions = summary.AnsweredQuestions,
                CorrectCount = summary.CorrectCount,
                IncorrectCount = summary.IncorrectCount,
                UnansweredCount = summary.UnansweredCount,
                EarnedPoints = summary.Score,
                MaxPoints = summary.MaxScore
            }
        };
    }

    private static void ValidateCursor(StudentResultCursorDto? cursor)
    {
        if (cursor is null)
            return;
        if (cursor.ReturnedAtUtc == default
            || cursor.ReturnedAtUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(cursor.ResultType)
            || cursor.ResultId == Guid.Empty)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Cursor kết quả không hợp lệ.");
        }
    }

    private static void AddParameter(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string SafeFileName(string value, Guid attachmentId)
    {
        var normalized = value.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized).Trim();
        fileName = string.Concat(fileName.Where(x => !char.IsControl(x)));
        return string.IsNullOrWhiteSpace(fileName)
            ? $"attachment-{attachmentId:N}"
            : fileName;
    }

    private static ApiException Integrity(string message) =>
        new(ErrorCodes.ValidationFailed, message);

    private sealed record ResultCandidate(
        StudentResultType ResultType,
        Guid ResultId,
        DateTimeOffset ReturnedAtUtc);
}
