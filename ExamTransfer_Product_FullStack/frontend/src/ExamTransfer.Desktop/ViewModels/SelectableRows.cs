using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public abstract class SelectableRow<T> : ObservableObject
{
    private bool isChecked;

    protected SelectableRow(T source) => Source = source;

    public T Source { get; }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (CanArchive && Set(ref isChecked, value))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public abstract bool CanArchive { get; }
    public event EventHandler? SelectionChanged;
}

public sealed class SelectableClassRow(ClassSummaryDto source)
    : SelectableRow<ClassSummaryDto>(source)
{
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Code => Source.Code;
    public string SchoolYear => Source.SchoolYear;
    public ClassStatus Status => Source.Status;
    public int StudentCount => Source.StudentCount;
    public override bool CanArchive => Source.Status != ClassStatus.Archived;
}

public sealed class SelectableExamRow(ExamSummaryDto source)
    : SelectableRow<ExamSummaryDto>(source)
{
    public Guid Id => Source.Id;
    public Guid? ClassId => Source.ClassId;
    public string Title => Source.Title;
    public string Subject => Source.Subject;
    public int DurationMinutes => Source.DurationMinutes;
    public ExamDeliveryType DeliveryType => Source.DeliveryType;
    public ExamStatus Status => Source.Status;
    public int FileCount => Source.FileCount;
    public override bool CanArchive => Source.Status is not (ExamStatus.Archived or ExamStatus.Cancelled);
}

public sealed class SelectableSessionRow(SessionSummaryDto source)
    : SelectableRow<SessionSummaryDto>(source)
{
    public Guid Id => Source.Id;
    public Guid ExamId => Source.ExamId;
    public string Title => Source.Title;
    public string RoomCode => Source.RoomCode;
    public SessionStatus Status => Source.Status;
    public SessionCountsDto Counts => Source.Counts;
    public SessionAccessMode AccessMode => Source.AccessMode;
    public bool AutoApprove => Source.AutoApprove;
    public string RowVersion => Source.RowVersion;
    public override bool CanArchive => Source.Status is SessionStatus.Finished or SessionStatus.Cancelled;
}
