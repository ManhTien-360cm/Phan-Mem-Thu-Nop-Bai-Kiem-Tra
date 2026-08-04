using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

internal sealed record QuizAuthoritativeScore(
    decimal Score,
    decimal MaxScore,
    int TotalQuestions,
    int AnsweredQuestions,
    int CorrectCount,
    int IncorrectCount,
    int UnansweredCount);

internal static class QuizGradeAuthoritativeScoring
{
    public const decimal RequiredMaxScore = 10.00m;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<QuizAuthoritativeScore> CalculateAsync(
        AppDbContext db,
        QuizAttempt attempt,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<QuizQuestionDto> snapshot;
        try
        {
            snapshot = ParseSnapshot(attempt.SnapshotJson);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw Integrity("Snapshot câu hỏi của bài làm không hợp lệ.");
        }

        if (snapshot.Count == 0
            || snapshot.Select(x => x.Id).Distinct().Count() != snapshot.Count
            || snapshot.Any(x => x.Id == Guid.Empty
                || x.Points <= 0
                || x.Points != decimal.Round(x.Points, 2, MidpointRounding.AwayFromZero)
                || x.Choices.Count == 0
                || x.Choices.Select(c => c.Id).Distinct().Count() != x.Choices.Count))
        {
            throw Integrity("Snapshot câu hỏi của bài làm không nhất quán.");
        }

        var questionIds = snapshot.Select(x => x.Id).ToList();
        var questions = await db.QuizQuestionsSet.AsNoTracking()
            .Include(x => x.Choices)
            .Where(x => questionIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (questions.Count != snapshot.Count)
            throw Integrity("Không tìm thấy đầy đủ câu hỏi authoritative của bài làm.");

        var authoritativeById = questions.ToDictionary(x => x.Id);
        foreach (var snapshotQuestion in snapshot)
        {
            if (!authoritativeById.TryGetValue(snapshotQuestion.Id, out var question)
                || question.ExamId != attempt.Session.ExamId
                || question.Version != attempt.ExamVersion
                || question.Points != snapshotQuestion.Points
                || question.Multiple != snapshotQuestion.Multiple)
            {
                throw Integrity("Câu hỏi authoritative không khớp đề và phiên bản của attempt.");
            }

            var snapshotChoiceIds = snapshotQuestion.Choices.Select(x => x.Id).ToHashSet();
            var authoritativeChoiceIds = question.Choices.Select(x => x.Id).ToHashSet();
            if (!snapshotChoiceIds.SetEquals(authoritativeChoiceIds)
                || question.Choices.Count(x => x.IsCorrect) == 0)
            {
                throw Integrity("Lựa chọn authoritative không khớp snapshot của attempt.");
            }
        }

        if (attempt.Answers.Any(x => x.AttemptId != attempt.Id)
            || attempt.Answers.GroupBy(x => x.QuestionId).Any(x => x.Count() > 1)
            || attempt.Answers.Any(x => !authoritativeById.ContainsKey(x.QuestionId)))
        {
            throw Integrity("Answer graph chứa answer không thuộc attempt hoặc bị trùng câu hỏi.");
        }

        var selectedByQuestion = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var answer in attempt.Answers)
        {
            List<Guid> selected;
            try
            {
                selected = JsonSerializer.Deserialize<List<Guid>>(answer.ChoiceIdsJson, Json)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw Integrity("Danh sách lựa chọn của answer không hợp lệ.");
            }

            if (selected.Count != selected.Distinct().Count())
                throw Integrity("Answer chứa lựa chọn trùng.");
            var question = authoritativeById[answer.QuestionId];
            var validChoiceIds = question.Choices.Select(x => x.Id).ToHashSet();
            if (selected.Any(x => !validChoiceIds.Contains(x))
                || (!question.Multiple && selected.Count > 1))
            {
                throw Integrity("Answer chứa lựa chọn không thuộc câu hỏi.");
            }
            selectedByQuestion.Add(answer.QuestionId, selected.ToHashSet());
        }

        var score = 0m;
        var answered = 0;
        var correct = 0;
        foreach (var question in questions)
        {
            selectedByQuestion.TryGetValue(question.Id, out var selected);
            selected ??= [];
            if (selected.Count == 0)
                continue;
            answered++;
            var correctChoices = question.Choices.Where(x => x.IsCorrect).Select(x => x.Id).ToHashSet();
            if (selected.SetEquals(correctChoices))
            {
                correct++;
                score += question.Points;
            }
        }

        var maxScore = questions.Sum(x => x.Points);
        if (maxScore != RequiredMaxScore
            || attempt.MaxScore != RequiredMaxScore
            || score < 0
            || score > maxScore
            || score != decimal.Round(score, 2, MidpointRounding.AwayFromZero))
        {
            throw Integrity("Thang điểm hoặc kết quả authoritative của attempt không hợp lệ.");
        }

        return new(
            score,
            maxScore,
            questions.Count,
            answered,
            correct,
            answered - correct,
            questions.Count - answered);
    }

    public static IReadOnlyList<QuizQuestionDto> ParseSnapshot(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException();
        return document.RootElement.EnumerateArray().Select(question =>
        {
            var choices = question.TryGetProperty("choices", out var choiceRows)
                && choiceRows.ValueKind == JsonValueKind.Array
                ? choiceRows.EnumerateArray().Select(choice => new QuizChoiceDto(
                    choice.GetProperty("id").GetGuid(),
                    StringProperty(choice, "text", "choiceText"),
                    IntProperty(choice, "order", "sortOrder"))).ToList()
                : throw new JsonException();
            return new QuizQuestionDto(
                question.GetProperty("id").GetGuid(),
                StringProperty(question, "text", "questionText"),
                IntProperty(question, "order", "sortOrder"),
                question.GetProperty("points").GetDecimal(),
                question.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean(),
                choices);
        }).ToList();
    }

    private static string StringProperty(JsonElement element, string primary, string fallback) =>
        element.TryGetProperty(primary, out var value)
            ? value.GetString() ?? string.Empty
            : element.TryGetProperty(fallback, out value)
                ? value.GetString() ?? string.Empty
                : throw new JsonException();

    private static int IntProperty(JsonElement element, string primary, string fallback) =>
        element.TryGetProperty(primary, out var value)
            ? value.GetInt32()
            : element.TryGetProperty(fallback, out value)
                ? value.GetInt32()
                : throw new JsonException();

    private static ApiException Integrity(string message) =>
        new(ErrorCodes.ValidationFailed, message);
}
