using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class QuizReviewPresentationModel
{
    public QuizReviewPresentationModel(QuizGradeDetailDto detail, bool reopened = false)
    {
        AttemptId = detail.AttemptId;
        StudentCode = detail.StudentCode;
        StudentName = detail.DisplayName;
        ExamTitle = detail.ExamTitle;
        AutoScore = detail.AutoScore;
        FinalScore = detail.Score;
        MaxScore = detail.MaxScore;
        Comment = detail.GeneralComment ?? string.Empty;
        Status = detail.Status;
        StatusText = GradingStatusPresentation.ToText(detail.Status, reopened);
        Questions = detail.Questions
            .OrderBy(question => question.Order)
            .Select(question => new QuizQuestionReviewRow(question))
            .ToArray();
    }

    public Guid AttemptId { get; }
    public string StudentCode { get; }
    public string StudentName { get; }
    public string ExamTitle { get; }
    public decimal? AutoScore { get; }
    public decimal? FinalScore { get; }
    public decimal MaxScore { get; }
    public string Comment { get; }
    public GradingStatus Status { get; }
    public string StatusText { get; }
    public IReadOnlyList<QuizQuestionReviewRow> Questions { get; }
    public bool HasNoQuestions => Questions.Count == 0;
    public string EmptyStateText => "Bài trắc nghiệm chưa có câu hỏi.";
}

public sealed record QuizQuestionReviewRow
{
    public QuizQuestionReviewRow(QuizQuestionReviewDto question)
    {
        Id = question.Id;
        Order = question.Order;
        Text = question.Text;
        Points = question.Points;
        EarnedPoints = question.EarnedPoints;
        OptionsText = JoinChoices(question.Choices);

        var selected = question.Choices.Where(choice => choice.Selected).ToArray();
        var correct = question.Choices.Where(choice => choice.Correct == true).ToArray();
        StudentSelectionText = selected.Length == 0 ? "Bỏ trống" : JoinChoices(selected);
        CorrectAnswerText = correct.Length > 0
            ? JoinChoices(correct)
            : question.Choices.All(choice => choice.Correct is null)
                ? "Chưa được cung cấp"
                : "Không có đáp án đúng";

        IsBlank = selected.Length == 0;
        IsCorrect = !IsBlank && EarnedPoints.HasValue && EarnedPoints.Value == Points;
        IsIncorrect = !IsBlank && EarnedPoints.HasValue && !IsCorrect;
        OutcomeText = IsBlank
            ? "Bỏ trống"
            : !EarnedPoints.HasValue
                ? "Chưa có kết quả"
                : IsCorrect ? "Đúng" : "Sai";
    }

    public Guid Id { get; }
    public int Order { get; }
    public string Text { get; }
    public decimal Points { get; }
    public decimal? EarnedPoints { get; }
    public string OptionsText { get; }
    public string StudentSelectionText { get; }
    public string CorrectAnswerText { get; }
    public string OutcomeText { get; }
    public bool IsCorrect { get; }
    public bool IsIncorrect { get; }
    public bool IsBlank { get; }

    private static string JoinChoices(IEnumerable<QuizChoiceReviewDto> choices) =>
        string.Join(Environment.NewLine, choices
            .OrderBy(choice => choice.Order)
            .Select(choice => $"{ChoiceLabel(choice.Order)}. {choice.Text}"));

    private static string ChoiceLabel(int order) =>
        order is >= 1 and <= 26 ? ((char)('A' + order - 1)).ToString() : order.ToString();
}
