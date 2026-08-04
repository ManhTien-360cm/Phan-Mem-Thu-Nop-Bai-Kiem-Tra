using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class SubmissionSelectionRow : ObservableObject
{
    private bool isSelected;

    public SubmissionSelectionRow(SubmissionSummaryDto submission) =>
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public SubmissionSummaryDto Submission { get; }
    public Guid SubmissionId => Submission.Id;
    public Guid ParticipantId => Submission.ParticipantId;
    public string StudentCode => Submission.StudentCode;
    public string StudentName => Submission.DisplayName;
    public int AttemptNumber => Submission.AttemptNumber;
    public DateTimeOffset SubmittedAt =>
        Submission.ServerReceivedAtUtc
        ?? Submission.ClientSubmittedAtUtc
        ?? DateTimeOffset.MinValue;
    public bool IsLate => Submission.IsLate;
    public bool ComputedIsLate => Submission.ComputedIsLate;
    public bool? LateOverride => Submission.LateOverride;
    public string LateDisplay => IsLate ? "Nộp muộn" : "Đúng hạn";
    public string LateSource => LateOverride.HasValue
        ? "Giáo viên ghi đè"
        : "Hệ thống tự động";
    public SubmissionStatus Status => Submission.Status;
    public string? ReceiptCode => Submission.ReceiptCode;
    public int CompletedFileCount =>
        Submission.Files.Count(file => file.TransferStatus == TransferStatus.Completed);
    public bool CanDownload => CompletedFileCount > 0;
}
