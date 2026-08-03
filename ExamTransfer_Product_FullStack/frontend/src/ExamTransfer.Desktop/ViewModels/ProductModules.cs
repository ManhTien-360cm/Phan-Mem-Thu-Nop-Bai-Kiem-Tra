using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class ClassManagementViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IDialogService archiveDialogs;
    private SelectableClassRow? selectedClass;
    private StudentDto? selectedStudent;
    private ClassEnrollmentRequestDto? selectedEnrollmentRequest;
    private string currentClassRowVersion = "1";
    private string name = string.Empty;
    private string code = string.Empty;
    private string schoolYear = $"{DateTime.Today.Year}-{DateTime.Today.Year + 1}";
    private string description = string.Empty;
    private string studentCode = string.Empty;
    private string studentName = string.Empty;
    private string studentEmail = string.Empty;
    private ClassAccessMode accessMode = ClassAccessMode.Private;
    private bool allVisibleChecked;

    public ClassManagementViewModel(
        IBackendClient api,
        IDialogService? archiveDialogs = null)
    {
        this.api = api;
        this.archiveDialogs = archiveDialogs ?? AppServices.Dialogs;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy && SelectedClass is not null);
        AddStudentCommand = new AsyncRelayCommand(AddStudentAsync, () => !IsBusy && SelectedClass is not null);
        SaveClassCommand = new AsyncRelayCommand(SaveClassAsync, () => !IsBusy && SelectedClass is not null);
        UpdateStudentCommand = new AsyncRelayCommand(UpdateStudentAsync, () => !IsBusy && SelectedClass is not null && SelectedStudent is not null);
        RemoveStudentCommand = new AsyncRelayCommand(RemoveStudentAsync, () => !IsBusy && SelectedClass is not null && SelectedStudent is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy && SelectedClass is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy && SelectedClass is not null);
        BulkArchiveCommand = new AsyncRelayCommand(
            BulkArchiveAsync,
            () => !IsBusy && SelectedArchiveCount is > 0 and <= 200);
        ToggleArchiveSelectionCommand = new RelayCommand<SelectableClassRow>(
            ToggleArchiveSelection,
            row => !IsBusy && row?.CanArchive == true);
        ToggleAllVisibleArchiveSelectionCommand = new RelayCommand(
            ToggleAllVisibleArchiveSelection,
            () => !IsBusy && Classes.Any(row => row.CanArchive));
        ApproveEnrollmentCommand = new AsyncRelayCommand(ApproveEnrollmentAsync, () => !IsBusy && SelectedClass is not null && SelectedEnrollmentRequest?.Status == "Pending");
        RejectEnrollmentCommand = new AsyncRelayCommand(RejectEnrollmentAsync, () => !IsBusy && SelectedClass is not null && SelectedEnrollmentRequest?.Status == "Pending");
    }

    public ObservableCollection<SelectableClassRow> Classes { get; } = new();
    public ObservableCollection<StudentDto> Students { get; } = new();
    public ObservableCollection<ClassEnrollmentRequestDto> EnrollmentRequests { get; } = new();
    public SelectableClassRow? SelectedClass { get => selectedClass; set { if (Set(ref selectedClass, value)) RaiseCommands(); } }
    public StudentDto? SelectedStudent
    {
        get => selectedStudent;
        set
        {
            if (Set(ref selectedStudent, value))
            {
                if (value is not null)
                {
                    StudentCode = value.StudentCode;
                    StudentName = value.DisplayName;
                    StudentEmail = value.Email ?? string.Empty;
                }
                RaiseCommands();
            }
        }
    }
    public ClassEnrollmentRequestDto? SelectedEnrollmentRequest
    {
        get => selectedEnrollmentRequest;
        set { if (Set(ref selectedEnrollmentRequest, value)) RaiseCommands(); }
    }
    public string Name { get => name; set => Set(ref name, value); }
    public string Code { get => code; set => Set(ref code, value); }
    public string SchoolYear { get => schoolYear; set => Set(ref schoolYear, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string StudentCode { get => studentCode; set => Set(ref studentCode, value); }
    public string StudentName { get => studentName; set => Set(ref studentName, value); }
    public string StudentEmail { get => studentEmail; set => Set(ref studentEmail, value); }
    public IReadOnlyList<ClassAccessMode> AccessModes { get; } = Enum.GetValues<ClassAccessMode>();
    public ClassAccessMode AccessMode { get => accessMode; set => Set(ref accessMode, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand AddStudentCommand { get; }
    public ICommand SaveClassCommand { get; }
    public ICommand UpdateStudentCommand { get; }
    public ICommand RemoveStudentCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public int SelectedArchiveCount => Classes.Count(row => row.IsChecked);
    public bool AllVisibleChecked => allVisibleChecked;
    public ICommand BulkArchiveCommand { get; }
    public ICommand ToggleArchiveSelectionCommand { get; }
    public ICommand ToggleAllVisibleArchiveSelectionCommand { get; }
    public ICommand ApproveEnrollmentCommand { get; }
    public ICommand RejectEnrollmentCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải danh sách lớp", "Danh sách lớp đã được cập nhật", async token =>
        {
            await RefreshClassesCoreAsync(SelectedClass?.Id, token);
        });
    }

    private async Task RefreshClassesCoreAsync(Guid? selectedId, CancellationToken ct)
    {
        var data = ApiGuard.Require(await api.GetClassesAsync(ct));
        ReplaceClasses(data.Items);
        SelectedClass = selectedId.HasValue
            ? Classes.FirstOrDefault(x => x.Id == selectedId.Value) ?? Classes.FirstOrDefault()
            : Classes.FirstOrDefault();
        if (SelectedClass is not null)
            await LoadDetailAsync(ct);
        else
        {
            Students.Clear();
            EnrollmentRequests.Clear();
        }
    }

    private Task OpenAsync() => RunAsync("Đang mở lớp", "Đã tải chi tiết lớp", LoadDetailAsync);

    private async Task LoadDetailAsync(CancellationToken ct)
    {
        if (SelectedClass is null) return;
        var detail = ApiGuard.Require(await api.GetAsync<ClassDetailDto>($"api/v1/classes/{SelectedClass.Id}", ct));
        Students.ReplaceWith(detail.Students);
        EnrollmentRequests.ReplaceWith(detail.EnrollmentRequests ?? []);
        Name = detail.Name;
        Code = detail.Code;
        SchoolYear = detail.SchoolYear;
        Description = detail.Description ?? string.Empty;
        AccessMode = detail.AccessMode;
        currentClassRowVersion = detail.RowVersion;
        SelectedStudent = Students.FirstOrDefault();
        SelectedEnrollmentRequest = EnrollmentRequests.FirstOrDefault(x => x.Status == "Pending")
            ?? EnrollmentRequests.FirstOrDefault();
    }

    private Task CreateAsync() => RunAsync("Đang tạo lớp", "Lớp học đã được tạo", async ct =>
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Code)) throw new InvalidOperationException("Tên lớp và mã lớp là bắt buộc.");
        var created = ApiGuard.Require(await api.PostAsync<CreateClassRequest, ClassDetailDto>("api/v1/classes", new(Name.Trim(), Code.Trim(), SchoolYear.Trim(), Description.Trim(), AccessMode), ct));
        await RefreshClassesCoreAsync(created.Id, ct);
    });


    private Task SaveClassAsync() => RunAsync("Đang lưu lớp", "Thông tin lớp đã được cập nhật", async ct =>
    {
        if (SelectedClass is null) return;
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Code)) throw new InvalidOperationException("Tên lớp và mã lớp là bắt buộc.");
        var updated = ApiGuard.Require(await api.PutAsync<UpdateClassRequest, ClassDetailDto>($"api/v1/classes/{SelectedClass.Id}", new(Name.Trim(), Code.Trim(), SchoolYear.Trim(), Description.Trim(), currentClassRowVersion, AccessMode), ct));
        await RefreshClassesCoreAsync(updated.Id, ct);
    });

    private Task UpdateStudentAsync() => RunAsync("Đang cập nhật học sinh", "Thông tin học sinh đã được cập nhật", async ct =>
    {
        if (SelectedClass is null || SelectedStudent is null) return;
        var updated = ApiGuard.Require(await api.PutAsync<UpdateStudentRequest, StudentDto>($"api/v1/classes/{SelectedClass.Id}/students/{SelectedStudent.Id}", new(StudentCode.Trim(), StudentName.Trim(), string.IsNullOrWhiteSpace(StudentEmail) ? null : StudentEmail.Trim(), SelectedStudent.MetadataJson), ct));
        var index = Students.IndexOf(SelectedStudent);
        if (index >= 0) Students[index] = updated;
        SelectedStudent = updated;
    });

    private Task RemoveStudentAsync() => RunAsync("Đang xóa học sinh khỏi lớp", "Học sinh đã được xóa khỏi lớp", async ct =>
    {
        if (SelectedClass is null || SelectedStudent is null || !AppServices.Dialogs.Confirm("Xóa học sinh", $"Xóa {SelectedStudent.DisplayName} khỏi lớp?")) return;
        var classId = SelectedClass.Id;
        _ = await api.DeleteAsync<object>($"api/v1/classes/{SelectedClass.Id}/students/{SelectedStudent.Id}", ct);
        await RefreshClassesCoreAsync(classId, ct);
    });

    private Task ExportAsync() => RunAsync("Đang xuất danh sách lớp", "Danh sách lớp đã được xuất", async ct =>
    {
        if (SelectedClass is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/classes/{SelectedClass.Id}/export", Path.Combine(folder, $"{SelectedClass.Code}-students.csv"), null, ct);
    });

    private Task AddStudentAsync() => RunAsync("Đang thêm học sinh", "Đã thêm học sinh vào lớp", async ct =>
    {
        if (SelectedClass is null) return;
        if (string.IsNullOrWhiteSpace(StudentCode) || string.IsNullOrWhiteSpace(StudentName)) throw new InvalidOperationException("Mã và họ tên học sinh là bắt buộc.");
        var student = ApiGuard.Require(await api.PostAsync<CreateStudentRequest, StudentDto>($"api/v1/classes/{SelectedClass.Id}/students", new(StudentCode.Trim(), StudentName.Trim(), string.IsNullOrWhiteSpace(StudentEmail) ? null : StudentEmail.Trim(), null), ct));
        await RefreshClassesCoreAsync(SelectedClass.Id, ct);
        StudentCode = StudentName = StudentEmail = string.Empty;
    });

    private Task ImportAsync()
    {
        var summary = "Không có dữ liệu được import";
        return RunAsync("Đang kiểm tra file import", () => summary, async ct =>
        {
            if (SelectedClass is null) return;
            var file = AppServices.Files.PickFile(
                "Danh sách sinh viên (*.csv;*.xlsx)|*.csv;*.xlsx|CSV (*.csv)|*.csv|Excel (*.xlsx)|*.xlsx");
            if (file is null)
            {
                summary = "Đã hủy chọn file import";
                return;
            }
            var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file, ct));
            var preview = ApiGuard.Require(await api.PostAsync<ImportPreviewRequest, ImportPreviewDto>(
                $"api/v1/classes/{SelectedClass.Id}/imports/preview",
                new(Path.GetFileName(file), base64, null),
                ct));
            if (!AppServices.Dialogs.Confirm(
                    "Xác nhận import",
                    $"Tổng {preview.TotalRows} dòng · hợp lệ {preview.ValidRows} · lỗi {preview.InvalidRows}. " +
                    "Các dòng lỗi sẽ được bỏ qua. Tiếp tục?"))
            {
                summary = $"Đã xem trước {preview.TotalRows} dòng; chưa import";
                return;
            }
            var result = ApiGuard.Require(await api.PostAsync<ImportCommitRequest, ImportCommitResultDto>(
                $"api/v1/classes/{SelectedClass.Id}/imports/commit",
                new(preview.PreviewToken, true),
                ct));
            summary = $"Import xong: thêm {result.Inserted}, bỏ qua {result.Skipped}, lỗi {result.Errors.Count}";
            await RefreshClassesCoreAsync(SelectedClass.Id, ct);
        });
    }

    private Task BulkArchiveAsync() => RunAsync(
        "Đang lưu trữ các lớp đã chọn",
        "Các lớp đã chọn đã được lưu trữ",
        async ct =>
    {
        var selected = Classes
            .Where(row => row.CanArchive && row.IsChecked)
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
            return;
        var examples = string.Join(", ", selected.Take(3).Select(row => row.Name));
        if (!archiveDialogs.Confirm(
                "Lưu trữ lớp",
                $"Xóa {selected.Count} lớp ({examples}) khỏi danh sách? Các mục sẽ được chuyển vào trạng thái lưu trữ và không còn xuất hiện trong danh sách mặc định."))
            return;
        _ = ApiGuard.Require(await api.PostAsync<BulkArchiveRequest, BulkArchiveResultDto>(
            "api/v1/classes/bulk-archive",
            new(selected.Select(row => row.Id).ToList()),
            ct));
        await RefreshClassesCoreAsync(null, ct);
    });

    private void ToggleArchiveSelection(SelectableClassRow? row)
    {
        if (row is null || !row.CanArchive || IsBusy)
            return;
        row.IsChecked = !row.IsChecked;
    }

    private void ToggleAllVisibleArchiveSelection()
    {
        if (IsBusy)
            return;
        var eligible = Classes.Where(row => row.CanArchive).ToList();
        if (eligible.Count == 0)
            return;
        var next = !eligible.All(row => row.IsChecked);
        foreach (var row in eligible)
            row.IsChecked = next;
        OnArchiveSelectionChanged();
    }

    private void ReplaceClasses(IEnumerable<ClassSummaryDto> items)
    {
        foreach (var row in Classes)
            row.SelectionChanged -= ArchiveSelectionChanged;
        Classes.Clear();
        foreach (var item in items)
        {
            var row = new SelectableClassRow(item);
            row.SelectionChanged += ArchiveSelectionChanged;
            Classes.Add(row);
        }
        allVisibleChecked = false;
        Raise(nameof(AllVisibleChecked));
        OnArchiveSelectionChanged();
    }

    private void ArchiveSelectionChanged(object? sender, EventArgs e) =>
        OnArchiveSelectionChanged();

    private void OnArchiveSelectionChanged()
    {
        Raise(nameof(SelectedArchiveCount));
        var eligible = Classes.Where(row => row.CanArchive).ToList();
        var nextAll = eligible.Count > 0 && eligible.All(row => row.IsChecked);
        if (allVisibleChecked != nextAll)
        {
            allVisibleChecked = nextAll;
            Raise(nameof(AllVisibleChecked));
        }
        RaiseCommands();
    }

    private Task ApproveEnrollmentAsync() => RunAsync("Đang duyệt ghi danh", "Yêu cầu ghi danh đã được duyệt trên PublicCloud", async ct =>
    {
        if (SelectedClass is null || SelectedEnrollmentRequest is null) return;
        var mutationKey = $"approve-enrollment:{SelectedEnrollmentRequest.Id:N}";
        var mutationId = GetMutationRequestId(mutationKey);
        var updated = ApiGuard.Require(await api.PostAsync<object, ClassEnrollmentRequestDto>(
            $"api/v1/classes/{SelectedClass.Id}/enrollment-requests/{SelectedEnrollmentRequest.Id}/approve",
            new TeacherMutationRequest(mutationId), ct));
        CompleteMutationRequest(mutationKey);
        var index = EnrollmentRequests.IndexOf(SelectedEnrollmentRequest);
        if (index >= 0) EnrollmentRequests[index] = updated;
        SelectedEnrollmentRequest = updated;
    });

    private Task RejectEnrollmentAsync() => RunAsync("Đang từ chối ghi danh", "Yêu cầu ghi danh đã được từ chối trên PublicCloud", async ct =>
    {
        if (SelectedClass is null || SelectedEnrollmentRequest is null) return;
        var mutationKey = $"reject-enrollment:{SelectedEnrollmentRequest.Id:N}";
        var mutationId = GetMutationRequestId(mutationKey);
        var updated = ApiGuard.Require(await api.PostAsync<object, ClassEnrollmentRequestDto>(
            $"api/v1/classes/{SelectedClass.Id}/enrollment-requests/{SelectedEnrollmentRequest.Id}/reject",
            new ReasonedTeacherMutationRequest("Giáo viên từ chối yêu cầu ghi danh.", mutationId), ct));
        CompleteMutationRequest(mutationKey);
        var index = EnrollmentRequests.IndexOf(SelectedEnrollmentRequest);
        if (index >= 0) EnrollmentRequests[index] = updated;
        SelectedEnrollmentRequest = updated;
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, OpenCommand, AddStudentCommand, SaveClassCommand, UpdateStudentCommand, RemoveStudentCommand, ExportCommand, ImportCommand, BulkArchiveCommand, ApproveEnrollmentCommand, RejectEnrollmentCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
        (ToggleArchiveSelectionCommand as RelayCommand<SelectableClassRow>)?.RaiseCanExecuteChanged();
        (ToggleAllVisibleArchiveSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class ExamManagementViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IDialogService archiveDialogs;
    private CancellationTokenSource? detailLoadCts;
    private long detailLoadGeneration;
    private SelectableExamRow? selectedExam;
    private FileDescriptorDto? selectedFile;
    private string currentExamRowVersion = "1";
    private bool currentAutoZip;
    private bool currentRequireAtLeastOneFile = true;
    private string title = string.Empty;
    private string subject = string.Empty;
    private string description = string.Empty;
    private string duration = "60";
    private string allowedExtensions = ".pdf,.docx,.zip,.cs,.java,.py";
    private ExamDeliveryType deliveryType = ExamDeliveryType.FileSubmission;
    private QuizResultPolicy quizResultPolicy = QuizResultPolicy.Hidden;
    private SupervisionMode supervisionMode = SupervisionMode.None;
    private bool currentHasCommittedQuizSource;
    private int currentQuizQuestionCount;
    private Guid? currentLegacyClassId;
    private bool allVisibleChecked;
    private bool isCreatingNew = true;

    public ExamManagementViewModel(
        IBackendClient api,
        IDialogService? archiveDialogs = null)
    {
        this.api = api;
        this.archiveDialogs = archiveDialogs ?? AppServices.Dialogs;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        NewExamCommand = new RelayCommand(() => EnterCreateMode(resetForm: true), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && IsCreatingNew);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => !IsBusy && CanPublish);
        CloneCommand = new AsyncRelayCommand(CloneAsync, () => !IsBusy && SelectedExam is not null);
        BulkArchiveCommand = new AsyncRelayCommand(
            BulkArchiveAsync,
            () => !IsBusy && SelectedArchiveCount is > 0 and <= 200);
        ToggleArchiveSelectionCommand = new RelayCommand<SelectableExamRow>(
            ToggleArchiveSelection,
            row => !IsBusy && row?.CanArchive == true);
        ToggleAllVisibleArchiveSelectionCommand = new RelayCommand(
            ToggleAllVisibleArchiveSelection,
            () => !IsBusy && Exams.Any(row => row.CanArchive));
        UploadCommand = new AsyncRelayCommand(UploadFileAsync, () => !IsBusy && SelectedExam is not null && IsFileSubmission);
        ImportQuizCommand = new AsyncRelayCommand(PreviewQuizAsync, () => !IsBusy && IsEditingExisting && IsMultipleChoice && IsPolicyEditable);
        CommitQuizCommand = new AsyncRelayCommand(CommitQuizAsync, () => !IsBusy && IsEditingExisting && IsMultipleChoice && IsPolicyEditable && QuizImport.HasPreview);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && IsEditingExisting && SelectedExam is not null);
        DeleteFileCommand = new AsyncRelayCommand(DeleteFileAsync, () => !IsBusy && SelectedExam is not null && SelectedFile is not null);
        DownloadFileCommand = new AsyncRelayCommand(DownloadFileAsync, () => !IsBusy && SelectedExam is not null && SelectedFile is not null);
    }

    public ObservableCollection<SelectableExamRow> Exams { get; } = new();
    public ObservableCollection<FileDescriptorDto> Files { get; } = new();
    public SelectableExamRow? SelectedExam
    {
        get => selectedExam;
        set
        {
            if (!Set(ref selectedExam, value))
                return;
            Raise(nameof(PublishHint));
            Raise(nameof(IsPolicyEditable));
            Raise(nameof(HasSelectedExam));
            RaiseCommands();
        }
    }
    public int SelectedArchiveCount => Exams.Count(row => row.IsChecked);
    public bool AllVisibleChecked => allVisibleChecked;
    public FileDescriptorDto? SelectedFile { get => selectedFile; set { if (Set(ref selectedFile, value)) RaiseCommands(); } }
    public string Title { get => title; set => Set(ref title, value); }
    public string Subject { get => subject; set => Set(ref subject, value); }
    public string Description { get => description; set => Set(ref description, value); }
    public string Duration { get => duration; set => Set(ref duration, value); }
    public string AllowedExtensions { get => allowedExtensions; set => Set(ref allowedExtensions, value); }
    public QuizImportViewState QuizImport { get; } = new();
    public ExamDeliveryType DeliveryType
    {
        get => deliveryType;
        set
        {
            if (!Set(ref deliveryType, value))
                return;
            if (value == ExamDeliveryType.MultipleChoice)
                SupervisionMode = SupervisionMode.Standard;
            else
                QuizResultPolicy = QuizResultPolicy.Hidden;
            QuizImport.Clear();
            Raise(nameof(IsFileSubmission));
            Raise(nameof(IsMultipleChoice));
            Raise(nameof(CanPublish));
            Raise(nameof(PublishHint));
            RaiseCommands();
        }
    }
    public bool IsFileSubmission
    {
        get => DeliveryType == ExamDeliveryType.FileSubmission;
        set { if (value && IsPolicyEditable) DeliveryType = ExamDeliveryType.FileSubmission; }
    }
    public bool IsMultipleChoice
    {
        get => DeliveryType == ExamDeliveryType.MultipleChoice;
        set { if (value && IsPolicyEditable) DeliveryType = ExamDeliveryType.MultipleChoice; }
    }
    public bool ShowScoreAfterSubmission
    {
        get => QuizResultPolicy == QuizResultPolicy.ShowAfterSubmission;
        set
        {
            var next = value ? QuizResultPolicy.ShowAfterSubmission : QuizResultPolicy.Hidden;
            if (Set(ref quizResultPolicy, next, nameof(QuizResultPolicy)))
                Raise(nameof(ShowScoreAfterSubmission));
        }
    }
    public QuizResultPolicy QuizResultPolicy
    {
        get => quizResultPolicy;
        private set
        {
            if (Set(ref quizResultPolicy, value))
                Raise(nameof(ShowScoreAfterSubmission));
        }
    }
    public bool UseSupervision
    {
        get => SupervisionMode == SupervisionMode.Standard;
        set
        {
            if (IsMultipleChoice)
                value = true;
            SupervisionMode = value ? SupervisionMode.Standard : SupervisionMode.None;
        }
    }
    public SupervisionMode SupervisionMode
    {
        get => supervisionMode;
        private set
        {
            if (Set(ref supervisionMode, value))
                Raise(nameof(UseSupervision));
        }
    }
    public bool IsCreatingNew => isCreatingNew;
    public bool IsEditingExisting => !isCreatingNew;
    public bool IsPolicyEditable => SelectedExam is null
        ? IsCreatingNew
        : SelectedExam.Status == ExamStatus.Draft;
    public bool HasSelectedExam => SelectedExam is not null;
    public bool CanPublish => SelectedExam is not null
        && SelectedExam.Status is not (ExamStatus.Archived or ExamStatus.Cancelled)
        && (DeliveryType == ExamDeliveryType.MultipleChoice
            ? currentHasCommittedQuizSource && currentQuizQuestionCount > 0
            : !currentRequireAtLeastOneFile || Files.Count > 0);
    public string PublishHint => DeliveryType == ExamDeliveryType.MultipleChoice
        ? currentHasCommittedQuizSource && currentQuizQuestionCount > 0
            ? $"Đã commit {currentQuizQuestionCount} câu từ nguồn Word/PDF; có thể phát hành."
            : "Cần preview và commit nguồn Word/PDF hợp lệ trước khi phát hành."
        : currentRequireAtLeastOneFile && Files.Count == 0
        ? "Cần tải lên và hoàn tất ít nhất một file đề trước khi phát hành."
        : "Bài kiểm tra đã đáp ứng quy tắc file để phát hành.";
    public ICommand RefreshCommand { get; }
    public ICommand NewExamCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand BulkArchiveCommand { get; }
    public ICommand ToggleArchiveSelectionCommand { get; }
    public ICommand ToggleAllVisibleArchiveSelectionCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand ImportQuizCommand { get; }
    public ICommand CommitQuizCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteFileCommand { get; }
    public ICommand DownloadFileCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải bài kiểm tra", "Danh sách bài kiểm tra đã được cập nhật", async token =>
        {
            await RefreshExamsCoreAsync(
                IsEditingExisting ? SelectedExam?.Id : null,
                token,
                preserveCreateForm: IsCreatingNew);
        });
    }

    public async Task LoadSelectedExamAsync()
    {
        if (IsDisposed) return;
        try
        {
            Status = "Đang tải chi tiết bài kiểm tra";
            StatusTone = "primary";
            await LoadSelectedAsync(DisposeToken);
            if (!IsDisposed)
            {
                Status = "Đã tải chi tiết bài kiểm tra";
                StatusTone = "success";
            }
        }
        catch (OperationCanceledException) when (DisposeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private async Task RefreshExamsCoreAsync(
        Guid? selectedId,
        CancellationToken ct,
        bool preserveCreateForm = false)
    {
        var exams = ApiGuard.Require(await api.GetExamsAsync(ct));
        ReplaceExams(exams.Items);
        SelectedExam = selectedId.HasValue
            ? Exams.FirstOrDefault(x => x.Id == selectedId.Value)
            : null;
        if (SelectedExam is not null)
            await LoadSelectedAsync(ct);
        else
            EnterCreateMode(resetForm: !preserveCreateForm);
    }

    private async Task LoadSelectedAsync(CancellationToken ct)
    {
        var target = SelectedExam;
        if (target is null) return;
        SetEditorMode(creatingNew: false);
        var generation = Interlocked.Increment(ref detailLoadGeneration);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, DisposeToken);
        var previous = Interlocked.Exchange(ref detailLoadCts, linked);
        previous?.Cancel();
        previous?.Dispose();
        ExamDetailDto detail;
        try
        {
            detail = ApiGuard.Require(await api.GetAsync<ExamDetailDto>($"api/v1/exams/{target.Id}", linked.Token));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref detailLoadCts, null, linked) == linked)
                linked.Dispose();
        }
        if (generation != Interlocked.Read(ref detailLoadGeneration)
            || !IsEditingExisting
            || SelectedExam?.Id != target.Id)
            return;
        Title = detail.Title;
        Subject = detail.Subject;
        Description = detail.Description ?? string.Empty;
        Duration = detail.DurationMinutes.ToString();
        AllowedExtensions = string.Join(',', detail.FileRule.AllowedExtensions);
        DeliveryType = detail.DeliveryType;
        QuizResultPolicy = detail.QuizResultPolicy;
        SupervisionMode = detail.SupervisionMode;
        currentHasCommittedQuizSource = detail.QuizSource is not null;
        currentQuizQuestionCount = detail.QuizQuestionCount;
        QuizImport.Clear();
        currentAutoZip = detail.FileRule.AutoZip;
        currentRequireAtLeastOneFile = detail.FileRule.RequireAtLeastOneFile;
        currentLegacyClassId = detail.ClassId;
        Files.ReplaceWith(detail.Files);
        SelectedFile = Files.FirstOrDefault();
        currentExamRowVersion = detail.RowVersion;
        Raise(nameof(CanPublish));
        Raise(nameof(PublishHint));
        Raise(nameof(IsPolicyEditable));
        Raise(nameof(HasSelectedExam));
        RaiseCommands();
    }

    private Task SaveAsync() => RunAsync("Đang lưu bài kiểm tra", "Bài kiểm tra đã được cập nhật", async ct =>
    {
        if (SelectedExam is null) return;
        if (!int.TryParse(Duration, out var minutes) || minutes <= 0) throw new InvalidOperationException("Thời lượng phải là số phút lớn hơn 0.");
        var rule = new FileRuleDto(AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), 100L * 1024 * 1024, 500L * 1024 * 1024, 20, currentAutoZip, currentRequireAtLeastOneFile);
        var updated = ApiGuard.Require(await api.PutAsync<UpdateExamRequest, ExamDetailDto>(
            $"api/v1/exams/{SelectedExam.Id}",
            new(
                currentLegacyClassId,
                Title.Trim(),
                Subject.Trim(),
                Description.Trim(),
                minutes,
                rule,
                currentExamRowVersion,
                DeliveryType,
                QuizResultPolicy,
                SupervisionMode),
            ct));
        await RefreshExamsCoreAsync(updated.Id, ct);
    });

    private Task DeleteFileAsync() => RunAsync("Đang xóa file đề", "File đề đã được xóa", async ct =>
    {
        if (SelectedExam is null || SelectedFile is null || !AppServices.Dialogs.Confirm("Xóa file đề", $"Xóa {SelectedFile.Name}?")) return;
        var examId = SelectedExam.Id;
        _ = await api.DeleteAsync<object>($"api/v1/exams/{SelectedExam.Id}/files/{SelectedFile.Id}", ct);
        await RefreshExamsCoreAsync(examId, ct);
    });

    private Task DownloadFileAsync() => RunAsync("Đang tải file đề", "File đề đã được lưu", async ct =>
    {
        if (SelectedExam is null || SelectedFile is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/exams/{SelectedExam.Id}/files/{SelectedFile.Id}/content", Path.Combine(folder, SelectedFile.Name), null, ct);
    });

    private Task CreateAsync() => RunAsync("Đang tạo bài kiểm tra", "Bài kiểm tra đã được tạo ở trạng thái nháp", async ct =>
    {
        if (!IsCreatingNew)
            return;
        if (!int.TryParse(Duration, out var minutes) || minutes <= 0) throw new InvalidOperationException("Thời lượng phải là số phút lớn hơn 0.");
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Subject)) throw new InvalidOperationException("Tiêu đề và môn học là bắt buộc.");
        var rule = new FileRuleDto(AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), 100L * 1024 * 1024, 500L * 1024 * 1024, 20, false, true);
        var exam = ApiGuard.Require(await api.PostAsync<CreateExamRequest, ExamDetailDto>(
            "api/v1/exams",
            new(
                null,
                Title.Trim(),
                Subject.Trim(),
                Description.Trim(),
                minutes,
                rule,
                DeliveryType,
                QuizResultPolicy,
                SupervisionMode),
            ct));
        await RefreshExamsCoreAsync(exam.Id, ct);
    });

    private Task PublishAsync() => RunAsync("Đang phát hành đề", "Bài kiểm tra đã được phát hành", async ct =>
    {
        if (SelectedExam is null || !AppServices.Dialogs.Confirm("Phát hành bài kiểm tra", "Sau khi phát hành, thay file đề sẽ tạo phiên bản mới. Tiếp tục?")) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, ExamDetailDto>($"api/v1/exams/{SelectedExam.Id}/publish", new { }, ct));
        await RefreshExamsCoreAsync(detail.Id, ct);
    });

    private Task CloneAsync() => RunAsync("Đang nhân bản", "Đã tạo bản sao bài kiểm tra", async ct =>
    {
        if (SelectedExam is null) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, ExamDetailDto>($"api/v1/exams/{SelectedExam.Id}/clone", new { }, ct));
        await RefreshExamsCoreAsync(detail.Id, ct);
    });

    private Task BulkArchiveAsync() => RunAsync(
        "Đang lưu trữ các bài kiểm tra đã chọn",
        "Các bài kiểm tra đã chọn đã được lưu trữ",
        async ct =>
    {
        var selected = Exams
            .Where(row => row.CanArchive && row.IsChecked)
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
            return;
        var examples = string.Join(", ", selected.Take(3).Select(row => row.Title));
        if (!archiveDialogs.Confirm(
                "Lưu trữ bài kiểm tra",
                $"Xóa {selected.Count} bài ({examples}) khỏi danh sách? Các mục sẽ được chuyển vào trạng thái lưu trữ và không còn xuất hiện trong danh sách mặc định. Bài có phiên đang hoạt động sẽ bị từ chối."))
            return;
        _ = ApiGuard.Require(await api.PostAsync<BulkArchiveRequest, BulkArchiveResultDto>(
            "api/v1/exams/bulk-archive",
            new(selected.Select(row => row.Id).ToList()),
            ct));
        await RefreshExamsCoreAsync(null, ct);
    });

    private void ToggleArchiveSelection(SelectableExamRow? row)
    {
        if (row is null || !row.CanArchive || IsBusy)
            return;
        row.IsChecked = !row.IsChecked;
    }

    private void ToggleAllVisibleArchiveSelection()
    {
        if (IsBusy)
            return;
        var eligible = Exams.Where(row => row.CanArchive).ToList();
        if (eligible.Count == 0)
            return;
        var next = !eligible.All(row => row.IsChecked);
        foreach (var row in eligible)
            row.IsChecked = next;
        OnExamArchiveSelectionChanged();
    }

    private void ReplaceExams(IEnumerable<ExamSummaryDto> items)
    {
        foreach (var row in Exams)
            row.SelectionChanged -= ExamArchiveSelectionChanged;
        Exams.Clear();
        foreach (var item in items)
        {
            var row = new SelectableExamRow(item);
            row.SelectionChanged += ExamArchiveSelectionChanged;
            Exams.Add(row);
        }
        allVisibleChecked = false;
        Raise(nameof(AllVisibleChecked));
        OnExamArchiveSelectionChanged();
    }

    private void ExamArchiveSelectionChanged(object? sender, EventArgs e) =>
        OnExamArchiveSelectionChanged();

    private void OnExamArchiveSelectionChanged()
    {
        Raise(nameof(SelectedArchiveCount));
        var eligible = Exams.Where(row => row.CanArchive).ToList();
        var nextAll = eligible.Count > 0 && eligible.All(row => row.IsChecked);
        if (allVisibleChecked != nextAll)
        {
            allVisibleChecked = nextAll;
            Raise(nameof(AllVisibleChecked));
        }
        RaiseCommands();
    }

    private Task UploadFileAsync() => RunAsync("Đang tải file đề", "File đề đã được tải và xác minh", async ct =>
    {
        if (SelectedExam is null) return;
        var file = AppServices.Files.PickFile("Tài liệu|*.pdf;*.docx;*.xlsx;*.pptx;*.zip;*.txt|Tất cả file|*.*");
        if (file is null) return;
        var info = new FileInfo(file);
        var sha = await ComputeShaAsync(file, ct);
        var init = ApiGuard.Require(await api.PostAsync<InitFileUploadRequest, InitFileUploadResponse>($"api/v1/exams/{SelectedExam.Id}/files/init", new(info.Name, info.Length, sha, "application/octet-stream", null), ct));
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, init.ChunkSizeBytes, true);
        var buffer = new byte[init.ChunkSizeBytes];
        for (var index = 0; index < init.TotalChunks; index++)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            await using var chunk = new MemoryStream(buffer, 0, read, false, true);
            _ = await api.UploadChunkAsync($"api/v1/exams/{SelectedExam.Id}/files/{init.FileId}/chunks/{index}", chunk, read, null, ct);
            Status = $"Đang tải file đề: {index + 1}/{init.TotalChunks} phần";
        }
        var descriptor = ApiGuard.Require(await api.PostAsync<FinalizeFileUploadRequest, FileDescriptorDto>($"api/v1/exams/{SelectedExam.Id}/files/{init.FileId}/finalize", new(sha), ct));
        await RefreshExamsCoreAsync(SelectedExam.Id, ct);
    });

    private Task PreviewQuizAsync() => RunAsync("Đang đọc nguồn trắc nghiệm", "Đã tạo bản xem trước; chưa thay đổi câu hỏi", async ct =>
    {
        if (SelectedExam is null) return;
        var path = AppServices.Files.PickFile("Nguồn trắc nghiệm Word/PDF|*.docx;*.pdf");
        if (path is null) return;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var preview = ApiGuard.Require(await api.PostAsync<QuizImportPreviewRequest, QuizImportPreviewDto>(
            $"api/v1/exams/{SelectedExam.Id}/quiz-import/preview",
            new(Path.GetFileName(path), Convert.ToBase64String(bytes)), ct));
        QuizImport.SelectedFileName = path;
        QuizImport.Preview = preview;
        Status = preview.Errors.Count == 0
            ? $"Preview {preview.QuestionCount} câu · tổng {preview.MaxScore:0.##} điểm"
            : $"Nguồn có {preview.Errors.Count} lỗi; chưa thể commit";
        StatusTone = preview.Errors.Count == 0 ? "success" : "danger";
        RaiseCommands();
    });

    private Task CommitQuizAsync() => RunAsync("Đang commit đề trắc nghiệm", "Nguồn và câu hỏi đã được commit an toàn", async ct =>
    {
        if (SelectedExam is null || QuizImport.Preview is not { } preview || preview.Errors.Count > 0)
            return;
        if (preview.WillReplaceExisting
            && !AppServices.Dialogs.Confirm(
                "Thay bộ câu hỏi hiện tại",
                "Commit sẽ thay toàn bộ câu hỏi của phiên bản hiện tại. Tiếp tục?"))
            return;
        _ = ApiGuard.Require(await api.PostAsync<QuizImportCommitRequest, QuizImportResultDto>(
            $"api/v1/exams/{SelectedExam.Id}/quiz-import/commit",
            new(preview.PreviewToken, preview.WillReplaceExisting, currentExamRowVersion),
            ct));
        QuizImport.Clear();
        await RefreshExamsCoreAsync(SelectedExam.Id, ct);
    });

    private void EnterCreateMode(bool resetForm)
    {
        Interlocked.Increment(ref detailLoadGeneration);
        var previous = Interlocked.Exchange(ref detailLoadCts, null);
        previous?.Cancel();
        previous?.Dispose();
        SelectedExam = null;
        SetEditorMode(creatingNew: true);
        Files.Clear();
        SelectedFile = null;
        currentExamRowVersion = "1";
        currentAutoZip = false;
        currentRequireAtLeastOneFile = true;
        currentHasCommittedQuizSource = false;
        currentQuizQuestionCount = 0;
        currentLegacyClassId = null;
        QuizImport.Clear();
        if (resetForm)
        {
            Title = string.Empty;
            Subject = string.Empty;
            Description = string.Empty;
            Duration = "60";
            AllowedExtensions = ".pdf,.docx,.zip,.cs,.java,.py";
            DeliveryType = ExamDeliveryType.FileSubmission;
            QuizResultPolicy = QuizResultPolicy.Hidden;
            SupervisionMode = SupervisionMode.None;
        }
        Raise(nameof(CanPublish));
        Raise(nameof(PublishHint));
        RaiseCommands();
    }

    private void SetEditorMode(bool creatingNew)
    {
        if (isCreatingNew == creatingNew)
            return;
        isCreatingNew = creatingNew;
        Raise(nameof(IsCreatingNew));
        Raise(nameof(IsEditingExisting));
        Raise(nameof(IsPolicyEditable));
        Raise(nameof(HasSelectedExam));
        RaiseCommands();
    }

    private static async Task<string> ComputeShaAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, PublishCommand, CloneCommand, BulkArchiveCommand, UploadCommand, ImportQuizCommand, CommitQuizCommand, SaveCommand, DeleteFileCommand, DownloadFileCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
        (NewExamCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleArchiveSelectionCommand as RelayCommand<SelectableExamRow>)?.RaiseCanExecuteChanged();
        (ToggleAllVisibleArchiveSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        detailLoadCts?.Cancel();
        detailLoadCts?.Dispose();
        detailLoadCts = null;
        base.Dispose();
    }
}

public sealed class SessionManagementViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IDialogService archiveDialogs;
    private ExamSummaryDto? selectedExam;
    private SelectableSessionRow? selectedSession;
    private string roomCode = string.Empty;
    private string capacity = "36";
    private bool autoApprove;
    private SessionAccessMode accessMode = SessionAccessMode.LanOnly;
    private bool allVisibleChecked;
    private readonly Func<TimeSpan, CancellationToken, Task> projectionDelay;
    private readonly int projectionPollAttempts;
    private Guid? projectionSessionId;
    private string projectionStatus = "Phiên LAN không cần PublicCloud projection.";
    private string projectionTone = "info";
    private bool canShareRoomCode = true;
    private bool canRetryProjection;
    private string createResult = "Kỳ thi đã mở và đang chờ học sinh";

    public SessionManagementViewModel(
        IBackendClient api,
        IDialogService? archiveDialogs = null,
        Func<TimeSpan, CancellationToken, Task>? projectionDelay = null,
        int projectionPollAttempts = 24)
    {
        this.api = api;
        this.archiveDialogs = archiveDialogs ?? AppServices.Dialogs;
        this.projectionDelay = projectionDelay ?? Task.Delay;
        this.projectionPollAttempts = Math.Max(1, projectionPollAttempts);
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && SelectedExam is not null);
        BulkArchiveCommand = new AsyncRelayCommand(
            BulkArchiveAsync,
            () => !IsBusy && SelectedArchiveCount is > 0 and <= 200);
        ToggleArchiveSelectionCommand = new RelayCommand<SelectableSessionRow>(
            ToggleArchiveSelection,
            row => !IsBusy && row?.CanArchive == true);
        ToggleAllVisibleArchiveSelectionCommand = new RelayCommand(
            ToggleAllVisibleArchiveSelection,
            () => !IsBusy && Sessions.Any(row => row.CanArchive));
        OpenCommand = new AsyncRelayCommand(() => TransitionAsync("open", "Phòng thi đã mở và sẵn sàng nhận học sinh"), () => !IsBusy && SelectedSession?.Status == SessionStatus.Draft);
        DistributeCommand = new AsyncRelayCommand(() => TransitionAsync("distribute", "Đề thi đã được phân phối"), () => !IsBusy && SelectedSession?.Status == SessionStatus.Waiting);
        StartCommand = new AsyncRelayCommand(() => TransitionAsync("start", "Phiên thi đã bắt đầu"), () => !IsBusy && (SelectedSession?.Status is SessionStatus.Waiting or SessionStatus.Distributing));
        PauseCommand = new AsyncRelayCommand(() => TransitionAsync("pause", "Phiên thi đã tạm dừng"), () => !IsBusy && SelectedSession?.Status == SessionStatus.InProgress);
        ResumeCommand = new AsyncRelayCommand(() => TransitionAsync("resume", "Phiên thi đã tiếp tục"), () => !IsBusy && SelectedSession?.Status == SessionStatus.Paused);
        CollectCommand = new AsyncRelayCommand(() => TransitionAsync("collect", "Hệ thống đang thu bài"), () => !IsBusy && (SelectedSession?.Status is SessionStatus.InProgress or SessionStatus.Paused));
        EndCommand = new AsyncRelayCommand(EndAsync, () => !IsBusy && (SelectedSession?.Status is SessionStatus.InProgress or SessionStatus.Paused or SessionStatus.Collecting));
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => !IsBusy && SelectedSession?.Status == SessionStatus.Draft);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy && SelectedSession?.Status is SessionStatus.Draft or SessionStatus.Waiting);
        RetryProjectionCommand = new AsyncRelayCommand(
            RetryProjectionAsync,
            () => !IsBusy && projectionSessionId.HasValue && CanRetryProjection);
    }

    public ObservableCollection<ExamSummaryDto> Exams { get; } = new();
    public ObservableCollection<SelectableSessionRow> Sessions { get; } = new();
    public ExamSummaryDto? SelectedExam { get => selectedExam; set { if (Set(ref selectedExam, value)) RaiseCommands(); } }
    public SelectableSessionRow? SelectedSession
    {
        get => selectedSession;
        set
        {
            if (!Set(ref selectedSession, value)) return;
            if (value is not null)
            {
                AutoApprove = value.AutoApprove;
                AccessMode = value.AccessMode;
            }
            RaiseCommands();
        }
    }
    public string RoomCode { get => roomCode; set => Set(ref roomCode, value); }
    public string Capacity { get => capacity; set => Set(ref capacity, value); }
    public bool AutoApprove { get => autoApprove; set => Set(ref autoApprove, value); }
    public IReadOnlyList<SessionAccessMode> AccessModes { get; } = Enum.GetValues<SessionAccessMode>();
    public SessionAccessMode AccessMode
    {
        get => accessMode;
        set
        {
            if (!Set(ref accessMode, value))
                return;
            if (value == SessionAccessMode.LanOnly)
                ApplyProjection(new(
                    Guid.Empty,
                    false,
                    true,
                    SyncStatus.LocalOnly,
                    "LAN_ONLY",
                    "Phiên LAN không cần PublicCloud projection.",
                    0));
        }
    }
    public string ProjectionStatus => projectionStatus;
    public string ProjectionTone => projectionTone;
    public bool CanShareRoomCode => canShareRoomCode;
    public bool CanRetryProjection => canRetryProjection;
    public int SelectedArchiveCount => Sessions.Count(row => row.IsChecked);
    public bool AllVisibleChecked => allVisibleChecked;
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand BulkArchiveCommand { get; }
    public ICommand ToggleArchiveSelectionCommand { get; }
    public ICommand ToggleAllVisibleArchiveSelectionCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand DistributeCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CollectCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RetryProjectionCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phòng thi", "Danh sách phòng thi đã được cập nhật", async token =>
        {
            await RefreshSessionsCoreAsync(SelectedExam?.Id, SelectedSession?.Id, token);
        });
    }

    private async Task RefreshSessionsCoreAsync(Guid? examId, Guid? sessionId, CancellationToken ct)
    {
        var exams = ApiGuard.Require(await api.GetExamsAsync(ct));
        var sessions = ApiGuard.Require(await api.GetSessionsAsync(ct));
        Exams.ReplaceWith(exams.Items.Where(x => x.Status == ExamStatus.Published));
        ReplaceSessions(sessions.Items);
        SelectedExam = examId.HasValue
            ? Exams.FirstOrDefault(x => x.Id == examId.Value) ?? Exams.FirstOrDefault()
            : Exams.FirstOrDefault();
        SelectedSession = sessionId.HasValue
            ? Sessions.FirstOrDefault(x => x.Id == sessionId.Value) ?? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault();
    }

    private Task CreateAsync() => RunAsync(
        "Đang tạo và mở kỳ thi",
        () => createResult,
        async ct =>
    {
        if (SelectedExam is null) return;
        if (!int.TryParse(Capacity, out var cap) || cap <= 0) throw new InvalidOperationException("Sức chứa phải lớn hơn 0.");
        var detail = ApiGuard.Require(await api.PostAsync<CreateSessionRequest, SessionDetailDto>(
            "api/v1/sessions/create-and-open",
            new(
                SelectedExam.Id,
                null,
                DateTimeOffset.UtcNow.AddMinutes(5),
                "{\"autoApprove\":false}",
                false,
                cap,
                string.IsNullOrWhiteSpace(RoomCode) ? null : RoomCode.Trim(),
                AccessMode,
                SessionAdmissionMode.OpenRequest),
            ct));
        RoomCode = detail.Summary.RoomCode;
        await RefreshSessionsCoreAsync(SelectedExam.Id, detail.Summary.Id, ct);
        projectionSessionId = detail.Summary.Id;
        if (detail.Summary.AccessMode == SessionAccessMode.PublicCloud)
        {
            createResult = "Phòng đã được lưu cục bộ; đang kiểm tra PublicCloud.";
            await AwaitProjectionAsync(detail.Summary.Id, ct);
            createResult = ProjectionStatus;
        }
        else
        {
            ApplyProjection(new(
                detail.Summary.Id,
                false,
                true,
                SyncStatus.LocalOnly,
                "LAN_ONLY",
                "Phiên LAN đã sẵn sàng trong mạng cục bộ.",
                0));
            createResult = "Kỳ thi LAN đã mở và đang chờ học sinh";
        }
    });

    private async Task RetryProjectionAsync()
    {
        if (!projectionSessionId.HasValue)
            return;
        await RunAsync(
            "Đang yêu cầu đồng bộ lại PublicCloud",
            () => ProjectionStatus,
            async ct =>
            {
                var state = ApiGuard.Require(await api.PostAsync<object, CloudProjectionReadinessView>(
                    $"api/v1/sessions/{projectionSessionId}/cloud-projection/retry",
                    new { },
                    ct));
                ApplyProjection(state);
                await AwaitProjectionAsync(projectionSessionId.Value, ct);
            });
    }

    private async Task AwaitProjectionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < projectionPollAttempts; attempt++)
        {
            var readiness = ApiGuard.Require(await api.GetAsync<CloudProjectionReadinessView>(
                $"api/v1/sessions/{sessionId}/cloud-projection",
                cancellationToken));
            ApplyProjection(readiness);
            if (readiness.Ready || readiness.Status is SyncStatus.Failed or SyncStatus.Conflict)
                return;
            if (attempt + 1 < projectionPollAttempts)
                await projectionDelay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        ApplyProjection(new(
            sessionId,
            true,
            false,
            SyncStatus.Pending,
            "PUBLICCLOUD_PROJECTION_TIMEOUT",
            "Đang đồng bộ PublicCloud; chưa được xác nhận sẵn sàng để chia sẻ mã phòng.",
            0));
    }

    private void ApplyProjection(CloudProjectionReadinessView readiness)
    {
        projectionStatus = readiness.Message;
        projectionTone = readiness.Ready
            ? "success"
            : readiness.Status is SyncStatus.Failed or SyncStatus.Conflict
                ? "danger"
                : "warning";
        canShareRoomCode = !readiness.Required || readiness.Ready;
        canRetryProjection = readiness.Required && !readiness.Ready
            && (readiness.Status is SyncStatus.Failed or SyncStatus.Conflict
                || readiness.Code == "PUBLICCLOUD_PROJECTION_TIMEOUT");
        Raise(nameof(ProjectionStatus));
        Raise(nameof(ProjectionTone));
        Raise(nameof(CanShareRoomCode));
        Raise(nameof(CanRetryProjection));
        RaiseCommands();
    }

    private Task TransitionAsync(string action, string success) => RunAsync("Đang cập nhật trạng thái phòng", success, async ct =>
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/{action}", new { }, ct));
        ReplaceSelected(detail.Summary);
    });

    private Task SaveSettingsAsync() => RunAsync("Đang lưu chế độ duyệt", "Chế độ duyệt học sinh đã được cập nhật", async ct =>
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.GetSessionAsync(SelectedSession.Id, ct));
        var approvePending = false;
        if (!detail.Summary.AutoApprove && AutoApprove && detail.Summary.Counts.Pending > 0)
        {
            approvePending = AppServices.Dialogs.Confirm(
                "Duyệt các yêu cầu đang chờ",
                $"Có {detail.Summary.Counts.Pending} học sinh đang chờ. Bạn có muốn duyệt toàn bộ khi bật tự động duyệt không?");
            if (!approvePending)
            {
                AutoApprove = false;
                Status = "Chưa thay đổi chế độ; các yêu cầu đang chờ được giữ nguyên";
                return;
            }
        }
        var settings = $"{{\"autoApprove\":{AutoApprove.ToString().ToLowerInvariant()}}}";
        var updated = ApiGuard.Require(await api.PutAsync<UpdateSessionRequest, SessionDetailDto>(
            $"api/v1/sessions/{detail.Summary.Id}",
            new(detail.PlannedStartUtc, settings, AutoApprove, detail.Capacity, detail.Summary.RowVersion, approvePending), ct));
        ReplaceSelected(updated.Summary);
    });

    private Task EndAsync() => RunAsync("Đang kết thúc phiên", "Phiên thi đã kết thúc và được khóa nghiệp vụ", async ct =>
    {
        if (SelectedSession is null || !AppServices.Dialogs.Confirm("Kết thúc phiên", "Hệ thống sẽ kiểm tra các bài đang tải lên. Tiếp tục kết thúc?")) return;
        var detail = ApiGuard.Require(await api.PostAsync<EndSessionRequest, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/end", new(true, "Giáo viên xác nhận kết thúc."), ct));
        ReplaceSelected(detail.Summary);
    });

    private Task CancelAsync() => RunAsync("Đang hủy phòng", "Phòng thi đã được hủy", async ct =>
    {
        if (SelectedSession is null || !AppServices.Dialogs.Confirm("Hủy phòng thi", "Hủy phòng thi đang ở trạng thái nháp?")) return;
        var detail = ApiGuard.Require(await api.PostAsync<EndSessionRequest, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/cancel", new(false, "Giáo viên hủy phòng nháp."), ct));
        ReplaceSelected(detail.Summary);
    });

    private void ReplaceSelected(SessionSummaryDto summary)
    {
        if (SelectedSession is null) return;
        var index = Sessions.IndexOf(SelectedSession);
        var row = new SelectableSessionRow(summary);
        row.SelectionChanged += SessionArchiveSelectionChanged;
        if (index >= 0)
        {
            SelectedSession.SelectionChanged -= SessionArchiveSelectionChanged;
            Sessions[index] = row;
        }
        SelectedSession = row;
    }

    private Task BulkArchiveAsync() => RunAsync(
        "Đang lưu trữ các phiên đã chọn",
        "Các phiên đã chọn đã được lưu trữ",
        async ct =>
        {
            var selected = Sessions
                .Where(row => row.CanArchive && row.IsChecked)
                .GroupBy(row => row.Id)
                .Select(group => group.First())
                .ToList();
            if (selected.Count == 0)
                return;
            var examples = string.Join(", ", selected.Take(3).Select(row => row.RoomCode));
            if (!archiveDialogs.Confirm(
                    "Lưu trữ phiên thi",
                    $"Xóa {selected.Count} phiên ({examples}) khỏi danh sách? Các mục sẽ được chuyển vào trạng thái lưu trữ và không còn xuất hiện trong danh sách mặc định. Chỉ phiên đã kết thúc hoặc đã hủy mới hợp lệ."))
                return;
            _ = ApiGuard.Require(await api.PostAsync<BulkArchiveRequest, BulkArchiveResultDto>(
                "api/v1/sessions/bulk-archive",
                new(selected.Select(row => row.Id).ToList()),
                ct));
            await RefreshSessionsCoreAsync(SelectedExam?.Id, null, ct);
        });

    private void ToggleArchiveSelection(SelectableSessionRow? row)
    {
        if (row is null || !row.CanArchive || IsBusy)
            return;
        row.IsChecked = !row.IsChecked;
    }

    private void ToggleAllVisibleArchiveSelection()
    {
        if (IsBusy)
            return;
        var eligible = Sessions.Where(row => row.CanArchive).ToList();
        if (eligible.Count == 0)
            return;
        var next = !eligible.All(row => row.IsChecked);
        foreach (var row in eligible)
            row.IsChecked = next;
        OnSessionArchiveSelectionChanged();
    }

    private void ReplaceSessions(IEnumerable<SessionSummaryDto> items)
    {
        foreach (var row in Sessions)
            row.SelectionChanged -= SessionArchiveSelectionChanged;
        Sessions.Clear();
        foreach (var item in items)
        {
            var row = new SelectableSessionRow(item);
            row.SelectionChanged += SessionArchiveSelectionChanged;
            Sessions.Add(row);
        }
        allVisibleChecked = false;
        Raise(nameof(AllVisibleChecked));
        OnSessionArchiveSelectionChanged();
    }

    private void SessionArchiveSelectionChanged(object? sender, EventArgs e) =>
        OnSessionArchiveSelectionChanged();

    private void OnSessionArchiveSelectionChanged()
    {
        Raise(nameof(SelectedArchiveCount));
        var eligible = Sessions.Where(row => row.CanArchive).ToList();
        var nextAll = eligible.Count > 0 && eligible.All(row => row.IsChecked);
        if (allVisibleChecked != nextAll)
        {
            allVisibleChecked = nextAll;
            Raise(nameof(AllVisibleChecked));
        }
        RaiseCommands();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, BulkArchiveCommand, OpenCommand, DistributeCommand, StartCommand, PauseCommand, ResumeCommand, CollectCommand, EndCommand, CancelCommand, SaveSettingsCommand, RetryProjectionCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
        (ToggleArchiveSelectionCommand as RelayCommand<SelectableSessionRow>)?.RaiseCanExecuteChanged();
        (ToggleAllVisibleArchiveSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record CloudProjectionReadinessView(
    Guid SessionId,
    bool Required,
    bool Ready,
    SyncStatus Status,
    string Code,
    string Message,
    int RetryCount);

public sealed class LobbyViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IRealtimeService realtime;
    private readonly TeacherRealtimeSessionBinding realtimeBinding;
    private readonly RealtimeRefreshDebouncer realtimeRefresh =
        new(TimeSpan.FromMilliseconds(150), "Lobby.RealtimeRefresh");
    private SessionSummaryDto? selectedSession;
    private ParticipantDto? selectedParticipant;
    private string message = "Kỳ thi sẽ bắt đầu trong 5 phút. Vui lòng kiểm tra thiết bị.";

    public LobbyViewModel(
        IBackendClient api,
        IRealtimeService? realtime = null)
    {
        this.api = api;
        this.realtime = realtime ?? new RealtimeService(AppServices.BaseUrl);
        realtimeBinding = new TeacherRealtimeSessionBinding(this.realtime);
        this.realtime.NotificationReceived += OnRealtimeNotification;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadSessionCommand = new AsyncRelayCommand(LoadSessionAsync, () => !IsBusy && SelectedSession is not null);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => !IsBusy && SelectedParticipant is not null);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => !IsBusy && SelectedParticipant is not null);
        BulkApproveCommand = new AsyncRelayCommand(BulkApproveAsync, () => !IsBusy && Participants.Count > 0);
        MessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !IsBusy && SelectedSession is not null);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && SelectedSession is not null);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<ParticipantDto> Participants { get; } = new();
    public SessionSummaryDto? SelectedSession
    {
        get => selectedSession;
        set
        {
            if (!Set(ref selectedSession, value))
                return;
            realtimeBinding
                .SelectAsync(value?.Id, DisposeToken)
                .SafeFireAndForget("Lobby.SelectRealtimeSession");
            RaiseCommands();
        }
    }
    public ParticipantDto? SelectedParticipant { get => selectedParticipant; set { if (Set(ref selectedParticipant, value)) RaiseCommands(); } }
    public string Message { get => message; set => Set(ref message, value); }
    public ICommand RefreshCommand { get; }
    public ICommand LoadSessionCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand BulkApproveCommand { get; }
    public ICommand MessageCommand { get; }
    public ICommand StartCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phòng chờ", "Phòng chờ đã được cập nhật", async token =>
        {
            var data = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(data.Items.Where(x => x.Status is SessionStatus.Waiting or SessionStatus.Draft or SessionStatus.Distributing));
            SelectedSession ??= Sessions.FirstOrDefault();
            await realtimeBinding.EnsureAsync(
                AppServices.AuthState.AccountAccessToken,
                SelectedSession?.Id,
                token);
            if (SelectedSession is not null)
                await LoadSessionCoreAsync(token);
        });
    }

    private Task LoadSessionAsync() => RunAsync("Đang tải học sinh", "Danh sách học sinh đã được cập nhật", LoadSessionCoreAsync);
    private async Task LoadSessionCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.GetSessionAsync(SelectedSession.Id, ct));
        Participants.ReplaceWith(detail.Participants);
        SelectedParticipant = Participants.FirstOrDefault();
    }

    private Task ApproveAsync() => RunAsync("Đang duyệt học sinh", "Học sinh đã được duyệt", async ct =>
    {
        if (SelectedSession is null || SelectedParticipant is null) return;
        var mutationKey = $"approve-participant:{SelectedSession.Id:N}:{SelectedParticipant.Id:N}";
        var mutationId = GetMutationRequestId(mutationKey);
        var updated = ApiGuard.Require(await api.PostAsync<TeacherMutationRequest, ParticipantDto>($"api/v1/sessions/{SelectedSession.Id}/participants/{SelectedParticipant.Id}/approve", new(mutationId), ct));
        CompleteMutationRequest(mutationKey);
        ReplaceParticipant(updated);
    });

    private Task RejectAsync() => RunAsync("Đang từ chối yêu cầu", "Yêu cầu tham gia đã bị từ chối", async ct =>
    {
        if (SelectedSession is null || SelectedParticipant is null || !AppServices.Dialogs.Confirm("Từ chối học sinh", $"Từ chối {SelectedParticipant.DisplayName}?")) return;
        var mutationKey = $"reject-participant:{SelectedSession.Id:N}:{SelectedParticipant.Id:N}";
        var mutationId = GetMutationRequestId(mutationKey);
        _ = ApiGuard.Require(await api.PostAsync<ReasonedTeacherMutationRequest, object>($"api/v1/sessions/{SelectedSession.Id}/participants/{SelectedParticipant.Id}/reject", new("Thông tin tham gia chưa hợp lệ.", mutationId), ct));
        CompleteMutationRequest(mutationKey);
        Participants.Remove(SelectedParticipant);
        SelectedParticipant = Participants.FirstOrDefault();
    });

    private Task BulkApproveAsync() => RunAsync("Đang duyệt hàng loạt", "Đã duyệt các học sinh đang chờ", async ct =>
    {
        if (SelectedSession is null) return;
        var ids = Participants.Where(x => x.Status == ParticipantStatus.PendingApproval).Select(x => x.Id).ToArray();
        var mutationKey = $"bulk-approve:{SelectedSession.Id:N}:{string.Join(",", ids.Order())}";
        var mutationId = GetMutationRequestId(mutationKey);
        var updated = ApiGuard.Require(await api.PostAsync<BulkApproveRequest, IReadOnlyList<ParticipantDto>>($"api/v1/sessions/{SelectedSession.Id}/participants/bulk-approve", new(ids, mutationId), ct));
        CompleteMutationRequest(mutationKey);
        Participants.ReplaceWith(updated);
    });

    private Task SendMessageAsync() => RunAsync("Đang gửi thông báo", "Thông báo đã được gửi tới phòng chờ", async ct =>
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(Message)) return;
        _ = ApiGuard.Require(await api.PostAsync<SendMessageRequest, MessageDto>($"api/v1/sessions/{SelectedSession.Id}/messages", new(null, MessageType.Information, Message.Trim()), ct));
    });

    private Task StartAsync() => RunAsync("Đang bắt đầu phiên", "Phiên thi đã bắt đầu", async ct =>
    {
        if (SelectedSession is null) return;
        var pending = Participants.Count(x => x.Status == ParticipantStatus.PendingApproval);
        if (pending > 0 && !AppServices.Dialogs.Confirm("Bắt đầu phiên", $"Còn {pending} học sinh chưa được duyệt. Vẫn bắt đầu?")) return;
        _ = ApiGuard.Require(await api.PostAsync<object, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/start", new { }, ct));
    });

    private void ReplaceParticipant(ParticipantDto updated)
    {
        var existing = Participants.FirstOrDefault(x => x.Id == updated.Id);
        if (existing is null) return;
        var index = Participants.IndexOf(existing);
        Participants[index] = updated;
        SelectedParticipant = updated;
    }

    private void OnRealtimeNotification(
        object? sender,
        StudentRealtimeNotification notification)
    {
        if (IsDisposed
            || SelectedSession is not { } session
            || notification.SessionId != session.Id
            || notification.EventName is not (
                RealtimeEvents.ParticipantJoined
                or RealtimeEvents.ParticipantApproved
                or RealtimeEvents.ParticipantConnectionChanged))
            return;

        void ScheduleRefresh() =>
            realtimeRefresh.Schedule(LoadSessionAsync);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ScheduleRefresh();
        else
            dispatcher.InvokeAsync(ScheduleRefresh);
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;
        realtime.NotificationReceived -= OnRealtimeNotification;
        realtimeRefresh.Dispose();
        realtimeBinding
            .StopAsync()
            .SafeFireAndForget("Lobby.DisconnectRealtime");
        base.Dispose();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadSessionCommand, ApproveCommand, RejectCommand, BulkApproveCommand, MessageCommand, StartCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class SubmissionCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IFolderDialogService folders;
    private readonly SubmissionBatchDownloader submissionDownloader;
    private readonly IRealtimeService realtime;
    private readonly TeacherRealtimeSessionBinding realtimeBinding;
    private readonly RealtimeRefreshDebouncer realtimeRefresh =
        new(TimeSpan.FromMilliseconds(150), "SubmissionCenter.RealtimeRefresh");
    private SessionSummaryDto? selectedSession;
    private SubmissionSelectionRow? selectedSubmission;
    private string reason = "File nộp chưa đúng quy định.";

    public SubmissionCenterViewModel(
        IBackendClient api,
        IRealtimeService? realtime = null,
        IFolderDialogService? folders = null)
    {
        this.api = api;
        this.folders = folders ?? AppServices.Folders;
        submissionDownloader = new(api);
        this.realtime = realtime ?? new RealtimeService(AppServices.BaseUrl);
        realtimeBinding = new TeacherRealtimeSessionBinding(this.realtime);
        this.realtime.NotificationReceived += OnRealtimeNotification;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadSubmissionsAsync, () => !IsBusy && SelectedSession is not null);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => !IsBusy && SelectedSubmission is not null);
        ResubmitCommand = new AsyncRelayCommand(ResubmitAsync, () => !IsBusy && SelectedSubmission is not null);
        CopyReceiptCommand = new RelayCommand(CopyReceipt);
        SelectAllCommand = new RelayCommand(
            SelectAll,
            () => !IsBusy && Submissions.Count > 0 && !AllVisibleSelected);
        ClearSelectionCommand = new RelayCommand(
            ClearSelection,
            () => !IsBusy && HasSelection);
        DownloadSelectedCommand = new AsyncRelayCommand(
            DownloadSelectedAsync,
            () => !IsBusy && HasSelection);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<SubmissionSelectionRow> Submissions { get; } = new();
    public SessionSummaryDto? SelectedSession
    {
        get => selectedSession;
        set
        {
            if (!Set(ref selectedSession, value))
                return;
            realtimeBinding
                .SelectAsync(value?.Id, DisposeToken)
                .SafeFireAndForget("SubmissionCenter.SelectRealtimeSession");
            RaiseCommands();
        }
    }
    public SubmissionSelectionRow? SelectedSubmission { get => selectedSubmission; set { if (Set(ref selectedSubmission, value)) RaiseCommands(); } }
    public string Reason { get => reason; set => Set(ref reason, value); }
    public int SelectedCount => Submissions.Count(row => row.IsSelected);
    public int DownloadableSelectedCount =>
        Submissions.Count(row => row.IsSelected && row.CanDownload);
    public bool HasSelection => SelectedCount > 0;
    public bool HasDownloadableSelection => DownloadableSelectedCount > 0;
    public bool AllVisibleSelected =>
        Submissions.Count > 0 && Submissions.All(row => row.IsSelected);
    public ICommand RefreshCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ResubmitCommand { get; }
    public ICommand CopyReceiptCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand DownloadSelectedCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải dữ liệu thu bài", "Trung tâm thu bài đã được cập nhật", async token =>
        {
            var sessions = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(sessions.Items);
            SelectedSession ??= Sessions.FirstOrDefault();
            await realtimeBinding.EnsureAsync(
                AppServices.AuthState.AccountAccessToken,
                SelectedSession?.Id,
                token);
            if (SelectedSession is not null)
                await LoadSubmissionsCoreAsync(token);
        });
    }

    private Task LoadSubmissionsAsync() => RunAsync("Đang tải bài nộp", "Danh sách bài nộp đã được cập nhật", LoadSubmissionsCoreAsync);
    private async Task LoadSubmissionsCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var selectedIds = Submissions
            .Where(row => row.IsSelected)
            .Select(row => row.SubmissionId)
            .ToHashSet();
        var focusedId = SelectedSubmission?.SubmissionId;
        var data = ApiGuard.Require(await api.GetSubmissionsAsync(SelectedSession.Id, ct));
        UnsubscribeSubmissionRows();
        var refreshedRows = data.Items
            .Select(item => new SubmissionSelectionRow(item)
            {
                IsSelected = selectedIds.Contains(item.Id)
            })
            .ToList();
        foreach (var row in refreshedRows)
            row.PropertyChanged += OnSubmissionRowPropertyChanged;
        Submissions.ReplaceWith(refreshedRows);
        SelectedSubmission = focusedId.HasValue
            ? Submissions.FirstOrDefault(row => row.SubmissionId == focusedId.Value)
                ?? Submissions.FirstOrDefault()
            : Submissions.FirstOrDefault();
        RaiseSelectionState();
    }

    private Task RejectAsync() => RunAsync("Đang từ chối bài", "Bài nộp đã bị từ chối và vẫn được lưu lịch sử", async ct =>
    {
        if (SelectedSubmission is null || !AppServices.Dialogs.Confirm("Từ chối bài nộp", $"Từ chối attempt {SelectedSubmission.AttemptNumber} của {SelectedSubmission.StudentName}?")) return;
        var mutationKey = $"reject-submission:{SelectedSubmission.SubmissionId:N}";
        var mutationId = GetMutationRequestId(mutationKey);
        _ = ApiGuard.Require(await api.PostAsync<RejectSubmissionRequest, object>($"api/v1/submissions/{SelectedSubmission.SubmissionId}/reject", new(Reason, mutationId), ct));
        CompleteMutationRequest(mutationKey);
        await LoadSubmissionsCoreAsync(ct);
    });

    private Task ResubmitAsync() => RunAsync("Đang cấp quyền nộp lại", "Học sinh đã được phép tạo attempt mới", async ct =>
    {
        if (SelectedSubmission is null) return;
        var mutationKey = $"allow-resubmit:{SelectedSubmission.ParticipantId:N}";
        var mutationId = GetMutationRequestId(mutationKey);
        _ = ApiGuard.Require(await api.PostAsync<AllowResubmitRequest, object>($"api/v1/participants/{SelectedSubmission.ParticipantId}/allow-resubmit", new(Reason, mutationId), ct));
        CompleteMutationRequest(mutationKey);
    });

    private async Task DownloadSelectedAsync()
    {
        var destinationFolder = folders.PickFolder();
        if (destinationFolder is null)
            return;

        var selectionSnapshot = Submissions
            .Where(row => row.IsSelected)
            .Select(row => row.Submission)
            .ToArray();
        SubmissionDownloadResult? result = null;
        await RunAsync(
            "Đang tải các bài đã chọn",
            () => BuildDownloadSummary(result),
            async ct => result = await submissionDownloader.DownloadAsync(
                selectionSnapshot,
                destinationFolder,
                ct));

        if (!IsDisposed && result?.FailedFileCount > 0)
            StatusTone = "warning";
    }

    private void SelectAll()
    {
        foreach (var row in Submissions)
            row.IsSelected = true;
    }

    private void ClearSelection()
    {
        foreach (var row in Submissions)
            row.IsSelected = false;
    }

    private static string BuildDownloadSummary(SubmissionDownloadResult? result)
    {
        if (result is null)
            return "Không có kết quả tải bài.";
        if (result.HasNoCompletedFiles)
            return "Các bài đã chọn không có file Completed để tải.";

        var summary = $"Hoàn tất: {result.FullySuccessfulSubmissionCount} bài thành công hoàn toàn, " +
            $"{result.SuccessfulFileCount} file thành công, {result.FailedFileCount} file lỗi.";
        if (result.Failures.Count == 0)
            return summary;

        var failures = string.Join(
            Environment.NewLine,
            result.Failures.Select(failure => $"- {failure.DisplayName}: {failure.Error}"));
        return summary + Environment.NewLine + "File lỗi:" + Environment.NewLine + failures;
    }

    private void OnSubmissionRowPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SubmissionSelectionRow.IsSelected))
            RaiseSelectionState();
    }

    private void RaiseSelectionState()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(DownloadableSelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(HasDownloadableSelection));
        Raise(nameof(AllVisibleSelected));
        RaiseCommands();
    }

    private void UnsubscribeSubmissionRows()
    {
        foreach (var row in Submissions)
            row.PropertyChanged -= OnSubmissionRowPropertyChanged;
    }

    private void CopyReceipt()
    {
        if (!string.IsNullOrWhiteSpace(SelectedSubmission?.ReceiptCode))
        {
            AppServices.Clipboard.SetText(SelectedSubmission.ReceiptCode);
            Status = "Mã biên nhận đã được sao chép";
            StatusTone = "success";
        }
    }

    private void OnRealtimeNotification(
        object? sender,
        StudentRealtimeNotification notification)
    {
        if (IsDisposed
            || SelectedSession is not { } session
            || notification.SessionId != session.Id
            || notification.EventName is not (
                RealtimeEvents.SubmissionStarted
                or RealtimeEvents.SubmissionAccepted
                or RealtimeEvents.SubmissionRejected))
            return;

        void ScheduleRefresh() =>
            realtimeRefresh.Schedule(LoadSubmissionsAsync);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ScheduleRefresh();
        else
            dispatcher.InvokeAsync(ScheduleRefresh);
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;
        UnsubscribeSubmissionRows();
        realtime.NotificationReceived -= OnRealtimeNotification;
        realtimeRefresh.Dispose();
        realtimeBinding
            .StopAsync()
            .SafeFireAndForget("SubmissionCenter.DisconnectRealtime");
        base.Dispose();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadCommand, RejectCommand, ResubmitCommand, DownloadSelectedCommand }.OfType<AsyncRelayCommand>())
            command.RaiseCanExecuteChanged();
        foreach (var command in new[] { SelectAllCommand, ClearSelectionCommand }.OfType<RelayCommand>())
            command.RaiseCanExecuteChanged();
    }
}

public sealed class ExportCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SessionSummaryDto? selectedSession;
    private ExportJobDto? selectedJob;
    private string namingPattern = "{class}/{studentCode}_{studentName}";
    private bool includeFiles = true;
    private bool includeManifest = true;
    private bool includeReceipts = true;
    private bool includeAudit;

    public ExportCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && SelectedSession is not null);
        PollCommand = new AsyncRelayCommand(PollAsync, () => !IsBusy && SelectedJob is not null);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => !IsBusy && SelectedJob is not null);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedJob?.Status == ExportStatus.Completed);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<ExportJobDto> Jobs { get; } = new();
    public SessionSummaryDto? SelectedSession { get => selectedSession; set { if (Set(ref selectedSession, value)) RaiseCommands(); } }
    public ExportJobDto? SelectedJob { get => selectedJob; set { if (Set(ref selectedJob, value)) RaiseCommands(); } }
    public string NamingPattern { get => namingPattern; set => Set(ref namingPattern, value); }
    public bool IncludeFiles { get => includeFiles; set => Set(ref includeFiles, value); }
    public bool IncludeManifest { get => includeManifest; set => Set(ref includeManifest, value); }
    public bool IncludeReceipts { get => includeReceipts; set => Set(ref includeReceipts, value); }
    public bool IncludeAudit { get => includeAudit; set => Set(ref includeAudit, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand PollCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DownloadCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phiên có thể xuất", "Trung tâm xuất dữ liệu đã sẵn sàng", async token =>
        {
            var sessions = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(sessions.Items);
            SelectedSession ??= Sessions.FirstOrDefault();
        });
    }

    private Task CreateAsync() => RunAsync("Đang tạo export job", "Export job đã được tạo", async ct =>
    {
        if (SelectedSession is null) return;
        var job = ApiGuard.Require(await api.PostAsync<CreateExportRequest, ExportJobDto>("api/v1/exports", new(SelectedSession.Id, IncludeFiles, IncludeManifest, IncludeReceipts, IncludeAudit, "zip", NamingPattern), ct));
        Jobs.Insert(0, job);
        SelectedJob = job;
    });

    private Task PollAsync() => RunAsync("Đang cập nhật tiến trình", "Tiến trình export đã được cập nhật", async ct =>
    {
        if (SelectedJob is null) return;
        var job = ApiGuard.Require(await api.GetAsync<ExportJobDto>($"api/v1/exports/{SelectedJob.Id}", ct));
        ReplaceJob(job);
    });

    private Task CancelAsync() => RunAsync("Đang hủy export", "Export job đã được hủy", async ct =>
    {
        if (SelectedJob is null) return;
        _ = await api.PostAsync<object, object>($"api/v1/exports/{SelectedJob.Id}/cancel", new { }, ct);
        ReplaceJob(SelectedJob with { Status = ExportStatus.Cancelled });
    });

    private Task DownloadAsync() => RunAsync("Đang tải file export", "File export đã được lưu", async ct =>
    {
        if (SelectedJob is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/exports/{SelectedJob.Id}/download", Path.Combine(folder, SelectedJob.OutputFileName ?? "ExamTransfer-export.zip"), null, ct);
    });

    private void ReplaceJob(ExportJobDto job)
    {
        var old = Jobs.FirstOrDefault(x => x.Id == job.Id);
        if (old is not null) Jobs[Jobs.IndexOf(old)] = job;
        SelectedJob = job;
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, PollCommand, CancelCommand, DownloadCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class ControlCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private SessionSummaryDto? selectedSession;
    private ViolationDto? selectedViolation;
    private bool fullscreen = true;
    private bool emergencyExit = true;
    private string focusRule = "WarnOnFocusLost";
    private string clipboardRule = "BlockPaste";
    private string blockedProcesses = "chrome.exe,msedge.exe,firefox.exe";
    private string networkRule = "LocalOnly";

    public ControlCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadControlAsync, () => !IsBusy && SelectedSession is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && SelectedSession is not null);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && SelectedSession is not null);
        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeAsync, () => !IsBusy && SelectedViolation is not null);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<DeviceControlStatusDto> Devices { get; } = new();
    public ObservableCollection<ViolationDto> Violations { get; } = new();
    public SessionSummaryDto? SelectedSession { get => selectedSession; set { if (Set(ref selectedSession, value)) RaiseCommands(); } }
    public ViolationDto? SelectedViolation { get => selectedViolation; set { if (Set(ref selectedViolation, value)) RaiseCommands(); } }
    public bool Fullscreen { get => fullscreen; set => Set(ref fullscreen, value); }
    public bool EmergencyExit { get => emergencyExit; set => Set(ref emergencyExit, value); }
    public string FocusRule { get => focusRule; set => Set(ref focusRule, value); }
    public string ClipboardRule { get => clipboardRule; set => Set(ref clipboardRule, value); }
    public string BlockedProcesses { get => blockedProcesses; set => Set(ref blockedProcesses, value); }
    public string NetworkRule { get => networkRule; set => Set(ref networkRule, value); }
    public ICommand RefreshCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand AcknowledgeCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải phiên thi", "Trung tâm kiểm soát đã sẵn sàng", async token =>
        {
            var data = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(data.Items.Where(x => x.Status is SessionStatus.Waiting or SessionStatus.InProgress or SessionStatus.Paused));
            SelectedSession ??= Sessions.FirstOrDefault();
            if (SelectedSession is not null) await LoadControlCoreAsync(token);
        });
    }

    private Task LoadControlAsync() => RunAsync("Đang tải policy và vi phạm", "Dữ liệu kiểm soát đã được cập nhật", LoadControlCoreAsync);
    private async Task LoadControlCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var policy = await api.GetAsync<ControlPolicyDto?>($"api/v1/sessions/{SelectedSession.Id}/control-policy", ct);
        if (policy?.Success == true && policy.Data is not null)
        {
            Fullscreen = policy.Data.Fullscreen;
            EmergencyExit = policy.Data.EmergencyExit;
            FocusRule = policy.Data.FocusRule;
            ClipboardRule = policy.Data.ClipboardRule;
            BlockedProcesses = string.Join(',', policy.Data.BlockedProcesses);
            NetworkRule = policy.Data.NetworkRule;
        }
        var devices = ApiGuard.Require(await api.GetAsync<IReadOnlyList<DeviceControlStatusDto>>($"api/v1/sessions/{SelectedSession.Id}/devices/control-status", ct));
        var violations = ApiGuard.Require(await api.GetAsync<PagedResult<ViolationDto>>($"api/v1/sessions/{SelectedSession.Id}/violations", ct));
        Devices.ReplaceWith(devices);
        Violations.ReplaceWith(violations.Items);
        SelectedViolation = Violations.FirstOrDefault();
    }

    private Task SaveAsync() => RunAsync("Đang lưu policy", "Policy kiểm soát đã được lưu thành phiên bản mới", async ct =>
    {
        if (SelectedSession is null) return;
        var request = new SaveControlPolicyRequest(Fullscreen, FocusRule, ClipboardRule, Array.Empty<string>(), BlockedProcesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), NetworkRule, EmergencyExit, 180, null);
        _ = ApiGuard.Require(await api.PutAsync<SaveControlPolicyRequest, ControlPolicyDto>($"api/v1/sessions/{SelectedSession.Id}/control-policy", request, ct));
    });

    private Task ApplyAsync() => RunAsync("Đang áp dụng policy", "Policy đã được gửi tới các thiết bị hỗ trợ", async ct =>
    {
        if (SelectedSession is null || !AppServices.Dialogs.Confirm("Áp dụng policy", "Gửi policy hiện tại tới toàn bộ thiết bị trong phiên?")) return;
        _ = await api.PostAsync<ApplyControlPolicyRequest, object>($"api/v1/sessions/{SelectedSession.Id}/control-policy/apply", new(null), ct);
        await LoadControlCoreAsync(ct);
    });

    private Task AcknowledgeAsync() => RunAsync("Đang đánh dấu vi phạm", "Vi phạm đã được ghi nhận là đã xử lý", async ct =>
    {
        if (SelectedViolation is null) return;
        _ = await api.PostAsync<object, object>($"api/v1/violations/{SelectedViolation.Id}/acknowledge", new { }, ct);
        await LoadControlCoreAsync(ct);
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadCommand, SaveCommand, ApplyCommand, AcknowledgeCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed class HistoryAuditViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private string search = string.Empty;

    public HistoryAuditViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<AuditLogDto> Audits { get; } = new();
    public string Search { get => search; set => Set(ref search, value); }
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải lịch sử và audit", "Lịch sử hệ thống đã được cập nhật", async token =>
        {
            var history = ApiGuard.Require(await api.GetAsync<PagedResult<SessionSummaryDto>>($"api/v1/history/sessions?search={Uri.EscapeDataString(Search)}", token));
            var audits = ApiGuard.Require(await api.GetAsync<PagedResult<AuditLogDto>>($"api/v1/audit-logs?search={Uri.EscapeDataString(Search)}", token));
            Sessions.ReplaceWith(history.Items);
            Audits.ReplaceWith(audits.Items);
        });
    }

    private Task ExportAsync() => RunAsync("Đang xuất audit", "Báo cáo audit đã được tạo", async ct =>
    {
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.PostDownloadFileAsync("api/v1/audit-logs/export", new Dictionary<string, string>(), Path.Combine(folder, "audit-log.csv"), null, ct);
    });

    protected override void RaiseCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class BackupCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private BackupDto? selectedBackup;
    private bool includeFiles = true;
    private bool encrypt;

    public BackupCenterViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsBusy && SelectedBackup is not null);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedBackup is not null);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedBackup is not null);
    }

    public ObservableCollection<BackupDto> Backups { get; } = new();
    public BackupDto? SelectedBackup { get => selectedBackup; set { if (Set(ref selectedBackup, value)) RaiseCommands(); } }
    public bool IncludeFiles { get => includeFiles; set => Set(ref includeFiles, value); }
    public bool Encrypt { get => encrypt; set => Set(ref encrypt, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DownloadCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải bản sao lưu", "Danh sách backup đã được cập nhật", async token =>
        {
            var data = ApiGuard.Require(await api.GetAsync<IReadOnlyList<BackupDto>>("api/v1/backups", token));
            Backups.ReplaceWith(data);
            SelectedBackup ??= Backups.FirstOrDefault();
        });
    }

    private Task CreateAsync() => RunAsync("Đang tạo backup", "Backup mới đã sẵn sàng", async ct =>
    {
        var backup = ApiGuard.Require(await api.PostAsync<CreateBackupRequest, BackupDto>("api/v1/backups", new(IncludeFiles, Encrypt, null), ct));
        Backups.Insert(0, backup);
        SelectedBackup = backup;
    });

    private Task ValidateAsync() => RunAsync("Đang kiểm tra checksum", "Checksum và schema backup hợp lệ", async ct =>
    {
        if (SelectedBackup is null) return;
        var backup = ApiGuard.Require(await api.PostAsync<object, BackupDto>($"api/v1/backups/{SelectedBackup.Id}/validate", new { }, ct));
        ReplaceBackup(backup);
    });

    private Task RestoreAsync() => RunAsync("Đang lên lịch khôi phục", "Khôi phục đã được lên lịch an toàn", async ct =>
    {
        if (SelectedBackup is null || !AppServices.Dialogs.Confirm("Khôi phục dữ liệu", "Ứng dụng sẽ tạo backup hiện tại và yêu cầu khởi động lại. Tiếp tục?")) return;
        _ = ApiGuard.Require(await api.PostAsync<RestoreBackupRequest, RestoreScheduledDto>($"api/v1/backups/{SelectedBackup.Id}/restore", new("RESTORE"), ct));
    });

    private Task DownloadAsync() => RunAsync("Đang tải backup", "File backup đã được lưu", async ct =>
    {
        if (SelectedBackup is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        await api.DownloadFileAsync($"api/v1/backups/{SelectedBackup.Id}/download", Path.Combine(folder, SelectedBackup.FileName), null, ct);
    });

    private void ReplaceBackup(BackupDto backup)
    {
        var old = Backups.FirstOrDefault(x => x.Id == backup.Id);
        if (old is not null) Backups[Backups.IndexOf(old)] = backup;
        SelectedBackup = backup;
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, ValidateCommand, RestoreCommand, DownloadCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

internal sealed class DeploymentSettingsConsoleViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private string serverPort = "5048";
    private bool useHttps;
    private bool discoveryEnabled = true;
    private string discoveryPort = DiscoveryProtocol.DefaultPort.ToString();
    private string storageRoot = @"C:\ProgramData\ExamTransfer";
    private string chunkSize = "4194304";
    private string maxUploads = "8";
    private bool cloudEnabled;
    private string supabaseUrl = string.Empty;
    private string supabasePublishableKey = string.Empty;
    private string organizationId = string.Empty;
    private string cloudEnvironment = "Development";
    private string cloudAccessMode = CloudAccessModes.UserSession;
    private bool cloudUseResumableUploads = true;
    private bool cloudSecretConfigured;
    private bool cloudAuthenticated;
    private string cloudAuthenticatedEmail = string.Empty;
    private string cloudEmail = string.Empty;
    private string cloudPassword = string.Empty;
    private string cloudConfigurationStatus = "Chưa cấu hình";
    private string diagnostics = "Chưa chạy chẩn đoán";
    private string cloudPreflight = "Chưa kiểm tra kết nối Supabase";

    public DeploymentSettingsConsoleViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(DisposeToken),
            () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        DiagnosticsCommand = new AsyncRelayCommand(
            DiagnosticsAsync,
            () => !IsBusy);
        CloudPreflightCommand = new AsyncRelayCommand(
            CloudPreflightAsync,
            () => !IsBusy && CloudEnabled);
        SyncCommand = new AsyncRelayCommand(
            SyncAsync,
            () => !IsBusy && CloudEnabled);
        CloudLoginCommand = new AsyncRelayCommand(
            CloudLoginAsync,
            () => !IsBusy
                && CloudEnabled
                && IsUserSessionMode
                && !string.IsNullOrWhiteSpace(CloudEmail)
                && !string.IsNullOrWhiteSpace(CloudPassword));
        CloudLogoutCommand = new AsyncRelayCommand(
            CloudLogoutAsync,
            () => !IsBusy && CloudAuthenticated);
        BrowseStorageCommand = new RelayCommand(BrowseStorage);
    }

    public IReadOnlyList<string> CloudAccessModesList { get; } =
        new[]
        {
            CloudAccessModes.UserSession,
            CloudAccessModes.TrustedServer
        };

    public string ServerPort { get => serverPort; set => Set(ref serverPort, value); }
    public bool UseHttps { get => useHttps; set => Set(ref useHttps, value); }
    public bool DiscoveryEnabled { get => discoveryEnabled; set => Set(ref discoveryEnabled, value); }
    public string DiscoveryPort { get => discoveryPort; set => Set(ref discoveryPort, value); }
    public string StorageRoot { get => storageRoot; set => Set(ref storageRoot, value); }
    public string ChunkSize { get => chunkSize; set => Set(ref chunkSize, value); }
    public string MaxUploads { get => maxUploads; set => Set(ref maxUploads, value); }

    public bool CloudEnabled
    {
        get => cloudEnabled;
        set
        {
            if (Set(ref cloudEnabled, value))
                RaiseCommands();
        }
    }

    public string SupabaseUrl { get => supabaseUrl; set => Set(ref supabaseUrl, value); }
    public string SupabasePublishableKey { get => supabasePublishableKey; set => Set(ref supabasePublishableKey, value); }
    public string OrganizationId { get => organizationId; set => Set(ref organizationId, value); }
    public string CloudEnvironment { get => cloudEnvironment; set => Set(ref cloudEnvironment, value); }

    public string CloudAccessMode
    {
        get => cloudAccessMode;
        set
        {
            if (Set(ref cloudAccessMode, value))
            {
                Raise(nameof(IsUserSessionMode));
                Raise(nameof(IsTrustedServerMode));
                Raise(nameof(CloudSessionStatus));
                RaiseCommands();
            }
        }
    }

    public bool IsUserSessionMode => string.Equals(
        CloudAccessMode,
        CloudAccessModes.UserSession,
        StringComparison.OrdinalIgnoreCase);

    public bool IsTrustedServerMode => !IsUserSessionMode;

    public bool CloudUseResumableUploads
    {
        get => cloudUseResumableUploads;
        set => Set(ref cloudUseResumableUploads, value);
    }

    public bool CloudSecretConfigured
    {
        get => cloudSecretConfigured;
        private set
        {
            if (Set(ref cloudSecretConfigured, value))
            {
                Raise(nameof(CloudSecretStatusText));
                Raise(nameof(CloudSessionStatus));
            }
        }
    }

    public string CloudSecretStatusText =>
        CloudSecretConfigured ? "Đã cấu hình" : "Chưa cấu hình";

    public bool CloudAuthenticated
    {
        get => cloudAuthenticated;
        private set
        {
            if (Set(ref cloudAuthenticated, value))
            {
                Raise(nameof(CloudSessionStatus));
                RaiseCommands();
            }
        }
    }

    public string CloudAuthenticatedEmail
    {
        get => cloudAuthenticatedEmail;
        private set
        {
            if (Set(ref cloudAuthenticatedEmail, value))
                Raise(nameof(CloudSessionStatus));
        }
    }

    public string CloudEmail
    {
        get => cloudEmail;
        set
        {
            if (Set(ref cloudEmail, value))
                RaiseCommands();
        }
    }

    public string CloudPassword
    {
        get => cloudPassword;
        set
        {
            if (Set(ref cloudPassword, value))
                RaiseCommands();
        }
    }

    public string CloudSessionStatus => IsTrustedServerMode
        ? CloudSecretConfigured
            ? "Máy chủ tin cậy đã có secret key"
            : "TrustedServer chưa có secret key"
        : CloudAuthenticated
            ? $"Đã đăng nhập: {CloudAuthenticatedEmail}"
            : "Chưa đăng nhập Supabase";

    public string CloudConfigurationStatus
    {
        get => cloudConfigurationStatus;
        private set => Set(ref cloudConfigurationStatus, value);
    }

    public string Diagnostics { get => diagnostics; private set => Set(ref diagnostics, value); }
    public string CloudPreflight { get => cloudPreflight; private set => Set(ref cloudPreflight, value); }

    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand CloudPreflightCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand CloudLoginCommand { get; }
    public ICommand CloudLogoutCommand { get; }
    public ICommand BrowseStorageCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync(
            "Đang tải cài đặt",
            "Cài đặt đã được tải",
            async token =>
            {
                var settings = ApiGuard.Require(
                    await api.GetSettingsAsync(token));
                ServerPort = settings.ServerPort.ToString();
                UseHttps = settings.UseHttps;
                DiscoveryEnabled = settings.DiscoveryEnabled;
                DiscoveryPort = DiscoveryProtocol.DefaultPort.ToString();
                StorageRoot = settings.StorageRootPath;
                ChunkSize = settings.ChunkSizeBytes.ToString();
                MaxUploads = settings.MaxConcurrentUploads.ToString();
                CloudEnabled = settings.CloudEnabled;
                SupabaseUrl = settings.SupabaseUrl ?? string.Empty;
                SupabasePublishableKey = settings.SupabasePublishableKey ?? string.Empty;
                OrganizationId = settings.OrganizationId ?? string.Empty;
                CloudEnvironment = settings.CloudEnvironment;
                CloudAccessMode = settings.CloudAccessMode;
                CloudUseResumableUploads = settings.CloudUseResumableUploads;
                CloudSecretConfigured = settings.CloudSecretConfigured;
                CloudConfigurationStatus = settings.CloudConfigurationStatus;
                CloudAuthenticated = settings.CloudAuthenticated;
                CloudAuthenticatedEmail = settings.CloudAuthenticatedEmail ?? string.Empty;
                if (string.IsNullOrWhiteSpace(CloudEmail))
                    CloudEmail = CloudAuthenticatedEmail;
                CurrentRowVersion = settings.RowVersion;

                if (CloudEnabled)
                    await RefreshCloudSessionStateAsync(token);
            });
    }

    private string CurrentRowVersion { get; set; } = "1";

    private Task SaveAsync() => RunAsync(
        "Đang lưu cài đặt",
        "Cài đặt đã được lưu; thay đổi cổng và thư mục sẽ áp dụng sau khi khởi động lại",
        async ct => _ = await SaveSettingsCoreAsync(ct));

    private async Task<SettingsDto> SaveSettingsCoreAsync(
        CancellationToken ct)
    {
        if (!int.TryParse(ServerPort, out var server)
            || !int.TryParse(ChunkSize, out var chunk)
            || !int.TryParse(MaxUploads, out var uploads))
        {
            throw new InvalidOperationException(
                "Cổng, chunk size và số upload phải là số hợp lệ.");
        }

        var request = new UpdateSettingsRequest(
            ServerPort: server,
            UseHttps: UseHttps,
            DiscoveryEnabled: DiscoveryEnabled,
            DiscoveryPort: DiscoveryProtocol.DefaultPort,
            StorageRootPath: StorageRoot,
            MinFreeBytes: 5L * 1024 * 1024 * 1024,
            ChunkSizeBytes: chunk,
            MaxConcurrentUploads: uploads,
            HeartbeatSeconds: 5,
            DisconnectAfterSeconds: 20,
            CloudEnabled: CloudEnabled,
            TemporaryHours: 24,
            LogsDays: 30,
            RowVersion: CurrentRowVersion,
            SupabaseUrl: NullIfWhiteSpace(SupabaseUrl),
            SupabasePublishableKey: NullIfWhiteSpace(SupabasePublishableKey),
            OrganizationId: NullIfWhiteSpace(OrganizationId),
            CloudEnvironment: CloudEnvironment.Trim(),
            CloudUseResumableUploads: CloudUseResumableUploads,
            CloudAccessMode: CloudAccessMode);

        var updated = ApiGuard.Require(
            await api.PutAsync<UpdateSettingsRequest, SettingsDto>(
                "api/v1/settings",
                request,
                ct));
        CurrentRowVersion = updated.RowVersion;
        CloudSecretConfigured = updated.CloudSecretConfigured;
        CloudConfigurationStatus = updated.CloudConfigurationStatus;
        CloudAuthenticated = updated.CloudAuthenticated;
        CloudAuthenticatedEmail = updated.CloudAuthenticatedEmail ?? string.Empty;
        return updated;
    }

    private Task DiagnosticsAsync() => RunAsync(
        "Đang chạy chẩn đoán",
        "Chẩn đoán hệ thống đã hoàn tất",
        async ct =>
        {
            var response = ApiGuard.Require(
                await api.GetAsync<object>("api/v1/system/diagnostics", ct));
            Diagnostics = response.ToString() ?? "Chẩn đoán hoàn tất";
        });

    private Task CloudPreflightAsync() => RunAsync(
        "Đang kiểm tra Supabase",
        "Kiểm tra Supabase đã hoàn tất",
        async ct =>
        {
            _ = await SaveSettingsCoreAsync(ct);
            var result = ApiGuard.Require(
                await api.GetAsync<CloudPreflightDto>(
                    "api/v1/cloud/preflight",
                    ct));
            CloudSecretConfigured = result.SecretConfigured;
            CloudAuthenticated = result.Authenticated;
            CloudAuthenticatedEmail = result.AuthenticatedEmail ?? string.Empty;
            CloudAccessMode = result.AccessMode;
            CloudConfigurationStatus = !result.Configured
                ? "Thiếu cấu hình"
                : result.CanSynchronize
                    ? result.Reachable
                        ? "Sẵn sàng đồng bộ"
                        : "Đã cấu hình nhưng chưa kết nối được"
                    : "Cần đăng nhập Supabase";

            var messages = new List<string>
            {
                $"Trạng thái: {CloudConfigurationStatus}",
                $"Chế độ truy cập: {result.AccessMode}",
                $"Phiên đăng nhập: {(result.Authenticated ? result.AuthenticatedEmail : "Chưa đăng nhập")}",
                $"Kiểu khóa: {result.KeyMode}",
                $"Chiến lược upload: {result.UploadStrategy}"
            };
            messages.AddRange(result.Errors.Select(x => "Lỗi: " + x));
            messages.AddRange(result.Warnings.Select(x => "Cảnh báo: " + x));
            CloudPreflight = string.Join(Environment.NewLine, messages);
        });

    private Task CloudLoginAsync() => RunAsync(
        "Đang đăng nhập Supabase",
        "Đăng nhập Supabase thành công",
        async ct =>
        {
            _ = await SaveSettingsCoreAsync(ct);
            var session = ApiGuard.Require(
                await api.PostAsync<LoginRequest, CloudSessionDto>(
                    "api/v1/cloud/auth/login",
                    new LoginRequest(CloudEmail.Trim(), CloudPassword),
                    ct));
            CloudPassword = string.Empty;
            ApplyCloudSession(session);
        });

    private Task CloudLogoutAsync() => RunAsync(
        "Đang đăng xuất Supabase",
        "Đã đăng xuất Supabase",
        async ct =>
        {
            _ = ApiGuard.Require(
                await api.PostAsync<object, object>(
                    "api/v1/cloud/auth/logout",
                    new { },
                    ct));
            ApplyCloudSession(new CloudSessionDto(
                false, null, null, null, OrganizationId, null));
        });

    private async Task RefreshCloudSessionStateAsync(CancellationToken ct)
    {
        var session = ApiGuard.Require(
            await api.GetAsync<CloudSessionDto>(
                "api/v1/cloud/auth/session",
                ct));
        ApplyCloudSession(session);
    }

    private void ApplyCloudSession(CloudSessionDto session)
    {
        CloudAuthenticated = session.Authenticated;
        CloudAuthenticatedEmail = session.Email ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(session.OrganizationId))
            OrganizationId = session.OrganizationId;
    }

    private Task SyncAsync() => RunAsync(
        "Đang yêu cầu đồng bộ cloud",
        "Các bản ghi đang chờ đã được đưa vào luồng đồng bộ",
        async ct =>
        {
            _ = ApiGuard.Require(
                await api.PostAsync<object, object>(
                    "api/v1/cloud/sync",
                    new { },
                    ct));
        });

    private void BrowseStorage()
    {
        var path = AppServices.Folders.PickFolder();
        if (path is not null)
            StorageRoot = path;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected override void RaiseCommands()
    {
        foreach (var command in new[]
                 {
                     RefreshCommand,
                     SaveCommand,
                     DiagnosticsCommand,
                     CloudPreflightCommand,
                     SyncCommand,
                     CloudLoginCommand,
                     CloudLogoutCommand
                 }.OfType<AsyncRelayCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
    }
}

public sealed class StudentWaitingViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly AppAuthSessionState authState;
    private readonly IStudentRealtimeService realtime;
    private readonly IStudentExamFlowCoordinator flow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan pollInterval;
    private readonly int maximumPollCycles;
    private readonly SemaphoreSlim resolveGate = new(1, 1);
    private readonly object eventSync = new();
    private readonly CancellationTokenSource lifecycle = new();
    private CancellationTokenSource? eventDebounce;
    private Task? pollingTask;
    private bool subscribed;
    private bool active;
    private long lastResolvedRevision = -1;
    private string? lastNavigationRoute;
    private ParticipantDto? participant;
    private SessionDetailDto? session;

    public StudentWaitingViewModel(
        IBackendClient api,
        StudentSessionState state,
        AppAuthSessionState authState,
        IStudentRealtimeService? realtime = null,
        IStudentExamFlowCoordinator? flow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollInterval = null,
        int maximumPollCycles = 120)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        this.realtime = realtime ?? AppServices.StudentRealtime;
        this.flow = flow ?? AppServices.StudentExamFlow;
        this.delay = delay ?? Task.Delay;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2.5);
        this.maximumPollCycles = Math.Max(1, maximumPollCycles);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAndResolveAsync("manual", DisposeToken),
            () => !IsBusy && state.HasSession);
        LeaveCommand = new RelayCommand(Leave);
    }

    public ParticipantDto? Participant
    {
        get => participant;
        private set
        {
            if (!Set(ref participant, value)) return;
            RaiseConnectionDetails();
        }
    }

    public SessionDetailDto? Session
    {
        get => session;
        private set
        {
            if (!Set(ref session, value)) return;
            Raise(nameof(RoomCodeDisplay));
            Raise(nameof(RoomCodeHint));
            Raise(nameof(SessionTitleDisplay));
        }
    }

    public string RoomCodeDisplay =>
        !string.IsNullOrWhiteSpace(state.RoomCode)
            ? state.RoomCode
            : "—";

    public string RoomCodeHint =>
        state.HasSession
            ? Session?.Summary.Title ?? FirstAvailable(state.ExamTitle, "Đang tải thông tin kỳ thi")
            : "Chưa tham gia phòng thi";

    public string CandidateNameDisplay =>
        FirstAvailable(
            Participant?.DisplayName,
            state.DisplayName,
            authState.CurrentAccount?.DisplayName,
            "Chưa có thông tin thí sinh");

    public string StudentCodeDisplay =>
        FirstAvailable(
            Participant?.StudentCode,
            state.StudentCode,
            authState.CurrentAccount?.StudentCode,
            "Chưa cập nhật");

    public string DeviceNameDisplay =>
        FirstAvailable(Participant?.MachineName, Environment.MachineName, "Thiết bị hiện tại");

    public string DeviceIdDisplay =>
        FirstAvailable(
            Participant?.DeviceId,
            authState.CurrentAccount?.DeviceId,
            $"{Environment.MachineName}-{Environment.UserName}");

    public string ParticipantStatusDisplay => Participant?.Status switch
    {
        ParticipantStatus.Connected => "Đã kết nối",
        ParticipantStatus.PendingApproval => "Đang chờ giáo viên duyệt",
        ParticipantStatus.Approved => "Đã được duyệt",
        ParticipantStatus.Rejected => "Yêu cầu tham gia bị từ chối",
        ParticipantStatus.Disconnected => "Mất kết nối",
        ParticipantStatus.NotConnected => "Chưa kết nối",
        _ => state.HasSession ? "Đang kiểm tra trạng thái" : "Chưa tham gia phòng"
    };

    public string ConnectionDetailDisplay
    {
        get
        {
            var mode = state.AccessMode == SessionAccessMode.PublicCloud
                ? "Kết nối Public Cloud"
                : "Kết nối mạng LAN";
            var connection = Participant?.ConnectionState switch
            {
                ConnectionState.Online => "trực tuyến",
                ConnectionState.Connecting => "đang kết nối",
                ConnectionState.Reconnecting => "đang kết nối lại",
                ConnectionState.Degraded => "kết nối không ổn định",
                ConnectionState.Offline => "ngoại tuyến",
                _ => state.HasSession ? "đang xác minh" : "chưa thiết lập"
            };
            return $"{mode} · {connection}";
        }
    }

    public string SessionTitleDisplay =>
        Session?.Summary.Title ?? FirstAvailable(state.ExamTitle, "Chưa có kỳ thi được chọn");

    public ICommand RefreshCommand { get; }
    public ICommand LeaveCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        active = true;
        SubscribeRealtime();
        if (!state.HasSession)
        {
            Status = "Chưa có phiên tham gia. Hãy kết nối phòng trước.";
            StatusTone = "warning";
            return;
        }
        await RefreshAndResolveAsync("initial", ct);
    }

    private async Task<StudentExamFlowResolution?> RefreshAndResolveAsync(
        string source,
        CancellationToken cancellationToken)
    {
        if (!active || IsDisposed || !state.HasSession)
            return null;
        await resolveGate.WaitAsync(cancellationToken);
        try
        {
            if (!active || IsDisposed || !state.HasSession)
                return null;
            IsBusy = true;
            Status = source switch
            {
                "poll" => "Đang kiểm tra dự phòng vì realtime có thể bị gián đoạn",
                "realtime" => "Đã nhận cập nhật realtime; đang xác minh trạng thái",
                _ => "Đang kiểm tra trạng thái duyệt"
            };
            StatusTone = source == "poll" ? "warning" : "primary";
            var resolution = await flow.ResolveAsync(
                StudentExamEntryPoint.CurrentExam,
                false,
                cancellationToken);
            if (!active || IsDisposed)
            {
                if (resolution.State is
                    StudentExamFlowState.RejectedOrExpired or
                    StudentExamFlowState.SessionFinished)
                    await realtime.StopAsync(CancellationToken.None);
                return resolution;
            }

            SyncDisplayFromState();
            var waiting = resolution.State is
                StudentExamFlowState.PendingApproval or
                StudentExamFlowState.ApprovedWaiting;
            if (waiting)
            {
                Status = source == "poll"
                    ? $"Polling dự phòng: {resolution.Message}"
                    : resolution.Message;
                StatusTone = source == "poll" ? "warning" : "info";
                EnsurePolling();
            }
            else
            {
                active = false;
                lifecycle.Cancel();
                if (resolution.RequiresStartConfirmation
                    && (lastNavigationRoute != resolution.RouteKey
                        || lastResolvedRevision != state.Revision))
                    flow.NavigateResolved(StudentExamEntryPoint.CurrentExam, resolution);
                lastNavigationRoute = resolution.RouteKey;
                if (resolution.State is
                    StudentExamFlowState.RejectedOrExpired or
                    StudentExamFlowState.SessionFinished)
                    await realtime.StopAsync(CancellationToken.None);
            }
            lastResolvedRevision = Math.Max(lastResolvedRevision, state.Revision);
            return resolution;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || lifecycle.IsCancellationRequested
            || IsDisposed)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                ReportFailure(ex);
                Status = $"Lỗi mạng khi cập nhật phòng chờ. {Status}";
            }
            throw;
        }
        finally
        {
            if (!IsDisposed)
                IsBusy = false;
            resolveGate.Release();
        }
    }

    private void Leave()
    {
        if (AppServices.Dialogs.Confirm("Rời phòng", "Rời phòng chờ và xóa thông tin phiên hiện tại?"))
        {
            active = false;
            lifecycle.Cancel();
            realtime.StopAsync().SafeFireAndForget("StudentRealtime.Leave");
            state.Reset();
            api.SetParticipantToken(null);
            Participant = null;
            Session = null;
            RaiseConnectionDetails();
            Status = "Đã rời phòng chờ";
            StatusTone = "info";
        }
    }

    private void SubscribeRealtime()
    {
        if (subscribed)
            return;
        subscribed = true;
        realtime.NotificationReceived += OnRealtimeNotification;
        realtime.EventReceived += OnRealtimeEvent;
    }

    private void OnRealtimeNotification(
        object? sender,
        StudentRealtimeNotification notification)
    {
        if (!active
            || IsDisposed
            || notification.SessionId != state.SessionId
            || (notification.ParticipantId.HasValue
                && notification.ParticipantId != state.ParticipantId)
            || !StudentExamFlowCoordinator.IsLifecycleProgressionEvent(notification.EventName)
            || (notification.Revision > 0
                && notification.Revision <= lastResolvedRevision))
            return;
        QueueRealtimeResolve();
    }

    private void OnRealtimeEvent(object? sender, string eventName)
    {
        if (!active
            || IsDisposed
            || !StudentExamFlowCoordinator.IsLifecycleProgressionEvent(eventName))
            return;
        QueueRealtimeResolve();
    }

    private void QueueRealtimeResolve()
    {
        CancellationTokenSource current;
        lock (eventSync)
        {
            eventDebounce?.Cancel();
            eventDebounce?.Dispose();
            current = CancellationTokenSource.CreateLinkedTokenSource(
                DisposeToken,
                lifecycle.Token);
            eventDebounce = current;
        }
        DebounceAndResolveAsync(current).SafeFireAndForget(
            "StudentWaitingViewModel.RealtimeResolve");
    }

    private async Task DebounceAndResolveAsync(CancellationTokenSource current)
    {
        try
        {
            await delay(TimeSpan.FromMilliseconds(120), current.Token);
            await RefreshAndResolveAsync("realtime", current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
    }

    private void EnsurePolling()
    {
        if (pollingTask is { IsCompleted: false } || lifecycle.IsCancellationRequested)
            return;
        pollingTask = PollAsync();
        pollingTask.SafeFireAndForget("StudentWaitingViewModel.Poll");
    }

    private async Task PollAsync()
    {
        var consecutiveFailures = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            DisposeToken,
            lifecycle.Token);
        for (var cycle = 0;
             cycle < maximumPollCycles
             && active
             && !linked.IsCancellationRequested;
             cycle++)
        {
            try
            {
                await delay(pollInterval, linked.Token);
                var resolution = await RefreshAndResolveAsync("poll", linked.Token);
                consecutiveFailures = 0;
                if (resolution is null
                    || resolution.State is not (
                        StudentExamFlowState.PendingApproval or
                        StudentExamFlowState.ApprovedWaiting))
                    return;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    if (!IsDisposed)
                    {
                        Status = "Polling phòng chờ đã dừng sau 3 lỗi mạng liên tiếp. Hãy kiểm tra mạng hoặc bấm Làm mới.";
                        StatusTone = "danger";
                    }
                    return;
                }
                await delay(
                    TimeSpan.FromSeconds(Math.Pow(2, consecutiveFailures)),
                    linked.Token);
            }
        }
        if (active && !IsDisposed)
        {
            Status = "Polling dự phòng đã đạt giới hạn. Realtime vẫn hoạt động; bấm Làm mới để kiểm tra ngay.";
            StatusTone = "warning";
        }
    }

    private void SyncDisplayFromState()
    {
        if (!state.SessionId.HasValue || !state.ParticipantId.HasValue)
            return;
        Participant = new ParticipantDto(
            Id: state.ParticipantId.Value,
            SessionId: state.SessionId.Value,
            StudentCode: state.StudentCode,
            DisplayName: state.DisplayName,
            DeviceId: Environment.MachineName + "-" + Environment.UserName,
            MachineName: Environment.MachineName,
            IpAddress: null,
            AppVersion: "1.0.0",
            Status: state.ParticipantStatus ?? ParticipantStatus.PendingApproval,
            LastSeenUtc: DateTimeOffset.UtcNow,
            DownloadStatus: DownloadStatus.NotStarted,
            SubmissionStatus: state.SubmissionStatus,
            ExtraTimeMinutes: 0,
            EffectiveDeadlineUtc: null,
            ConnectionState: realtime.IsConnected
                ? ConnectionState.Online
                : ConnectionState.Reconnecting,
            ResubmitAllowed: state.ResubmitAllowed);
        Raise(nameof(RoomCodeHint));
        Raise(nameof(SessionTitleDisplay));
    }

    private void RaiseConnectionDetails()
    {
        Raise(nameof(RoomCodeDisplay));
        Raise(nameof(RoomCodeHint));
        Raise(nameof(CandidateNameDisplay));
        Raise(nameof(StudentCodeDisplay));
        Raise(nameof(DeviceNameDisplay));
        Raise(nameof(DeviceIdDisplay));
        Raise(nameof(ParticipantStatusDisplay));
        Raise(nameof(ConnectionDetailDisplay));
        Raise(nameof(SessionTitleDisplay));
    }

    private static string FirstAvailable(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    protected override void RaiseCommands() => (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    public override void Dispose()
    {
        if (IsDisposed)
            return;
        active = false;
        realtime.NotificationReceived -= OnRealtimeNotification;
        realtime.EventReceived -= OnRealtimeEvent;
        lock (eventSync)
        {
            eventDebounce?.Cancel();
            eventDebounce?.Dispose();
            eventDebounce = null;
        }
        lifecycle.Cancel();
        lifecycle.Dispose();
        base.Dispose();
    }
}

public sealed class StudentDownloadViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private FileDescriptorDto? selectedFile;
    private string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ExamTransfer", "Exam");
    private double progress;

    public StudentDownloadViewModel(IBackendClient api, StudentSessionState state)
    {
        this.api = api;
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        BrowseCommand = new RelayCommand(Browse);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedFile is not null);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => !IsBusy && Files.Count > 0);
    }

    public ObservableCollection<FileDescriptorDto> Files { get; } = new();
    public FileDescriptorDto? SelectedFile { get => selectedFile; set { if (Set(ref selectedFile, value)) RaiseCommands(); } }
    public string Destination { get => destination; set => Set(ref destination, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public ICommand RefreshCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand DownloadAllCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (!state.SessionId.HasValue)
        {
            Status = "Hãy tham gia phòng trước khi nhận đề.";
            StatusTone = "warning";
            return;
        }
        await RunAsync("Đang tải manifest", "Manifest đề thi đã được cập nhật", async token =>
        {
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                if (!state.ExamId.HasValue) throw new InvalidOperationException("Phiên PublicCloud chưa có ExamId.");
                Files.ReplaceWith(await AppServices.PublicCloud.ListExamFilesAsync(state.SessionId!.Value, token));
                SelectedFile = Files.FirstOrDefault();
                return;
            }
            api.SetParticipantToken(state.AccessToken);
            var session = ApiGuard.Require(await api.GetSessionAsync(state.SessionId.Value, token));
            state.ExamId = session.Summary.ExamId;
            var manifest = ApiGuard.Require(await api.GetAsync<ExamManifestDto>($"api/v1/exams/{session.Summary.ExamId}/manifest", token));
            Files.ReplaceWith(manifest.Files);
            SelectedFile = Files.FirstOrDefault();
        });
    }

    private void Browse()
    {
        var folder = AppServices.Folders.PickFolder();
        if (folder is not null) Destination = folder;
    }

    private Task DownloadAsync() => RunAsync("Đang tải file đề", "File đề đã được tải về", async ct =>
    {
        if (SelectedFile is null || !state.ExamId.HasValue) return;
        Directory.CreateDirectory(Destination);
        var destinationPath = Path.Combine(
            Destination,
            SubmissionBatchDownloader.MakeSafePathComponent(
                SelectedFile.Name,
                $"exam-file-{SelectedFile.Id:N}",
                160));
        if (state.AccessMode == SessionAccessMode.PublicCloud)
        {
            var signed = await AppServices.PublicCloud.GetExamFileUrlAsync(state.SessionId!.Value, SelectedFile.Id, ct);
            await AppServices.PublicCloud.DownloadVerifiedAsync(signed, destinationPath, ct);
            Progress = 100;
            return;
        }
        var reporter = new Progress<double>(x => Progress = x);
        await api.DownloadVerifiedFileAsync($"api/v1/exams/{state.ExamId}/files/{SelectedFile.Id}/content", destinationPath, SelectedFile.Sha256, reporter, ct);
    });

    private Task DownloadAllAsync() => RunAsync("Đang tải toàn bộ đề", "Tất cả file đề đã được tải về", async ct =>
    {
        if (!state.ExamId.HasValue) return;
        Directory.CreateDirectory(Destination);
        var index = 0;
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            index++;
            var safeFileName = SubmissionBatchDownloader.MakeSafePathComponent(
                file.Name,
                $"exam-file-{file.Id:N}",
                160);
            safeFileName = SubmissionBatchDownloader.MakeUniqueFileName(
                safeFileName,
                usedFileNames);
            var destinationPath = Path.Combine(Destination, safeFileName);
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                var signed = await AppServices.PublicCloud.GetExamFileUrlAsync(state.SessionId!.Value, file.Id, ct);
                await AppServices.PublicCloud.DownloadVerifiedAsync(signed, destinationPath, ct);
            }
            else
            {
                await api.DownloadVerifiedFileAsync($"api/v1/exams/{state.ExamId}/files/{file.Id}/content", destinationPath, file.Sha256, null, ct);
            }
            Progress = index * 100d / Files.Count;
        }
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, DownloadCommand, DownloadAllCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed record WorkspaceFileRow(string Name, string Size, string Modified, string Status);

public sealed class StudentSubmissionViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly AppAuthSessionState authState;
    private readonly ISubmissionRecoveryService submissionRecovery;
    private string? selectedPath;
    private double progress;
    private Guid? queueId;
    private string? queueError;
    private string fileName = "Chưa chọn file";
    private string fileType = "-";
    private string fileSize = "-";
    private string sha256 = "Chưa tính";
    private string validationStatus = "Chưa kiểm tra";
    private bool isFileValid;
    private bool hasActiveQueue;
    private bool receiptReceived;
    private Guid? trackedQueueSessionId;
    private Guid? trackedQueueParticipantId;
    private bool progressSubscribed;

    public StudentSubmissionViewModel(
        IBackendClient api,
        StudentSessionState state,
        AppAuthSessionState authState,
        ISubmissionRecoveryService? submissionRecovery = null)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        this.submissionRecovery = submissionRecovery ?? AppServices.SubmissionRecovery;
        PickCommand = new AsyncRelayCommand(PickAsync, () => !IsBusy);
        SubmitCommand = new AsyncRelayCommand(
            SubmitAsync,
            () => EvaluateEligibility().Allowed);
        state.PropertyChanged += OnSessionStatePropertyChanged;
    }

    public string? SelectedPath { get => selectedPath; private set { if (Set(ref selectedPath, value)) RaiseCommands(); } }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public Guid? QueueId { get => queueId; private set => Set(ref queueId, value); }
    public string? QueueError { get => queueError; private set => Set(ref queueError, value); }
    public string FileName { get => fileName; private set => Set(ref fileName, value); }
    public string FileType { get => fileType; private set => Set(ref fileType, value); }
    public string FileSize { get => fileSize; private set => Set(ref fileSize, value); }
    public string Sha256 { get => sha256; private set => Set(ref sha256, value); }
    public string ValidationStatus { get => validationStatus; private set => Set(ref validationStatus, value); }
    public string LimitText => "10 MB · 1 file · ZIP/RAR/7Z";
    public bool IsFileValid { get => isFileValid; private set { if (Set(ref isFileValid, value)) RaiseCommands(); } }
    public ICommand PickCommand { get; }
    public ICommand SubmitCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        Status = state.HasSession ? "Chọn một file nén ZIP, RAR hoặc 7Z để nộp" : "Hãy tham gia phòng trước khi nộp bài";
        StatusTone = state.HasSession ? "info" : "warning";
        if (state.HasSession)
        {
            var pending = Infrastructure.SubmissionQueueStore.FindActiveQueue(
                await Infrastructure.SubmissionQueueStore.LoadAsync(ct),
                state.SessionId!.Value,
                state.ParticipantId!.Value);
            if (pending is not null)
            {
                SelectedPath = pending.FilePath;
                FileName = pending.FileName;
                FileType = Path.GetExtension(pending.FileName).TrimStart('.').ToUpperInvariant();
                FileSize = FormatBytes(pending.SizeBytes);
                Sha256 = pending.Sha256;
                IsFileValid = pending.QueueStatus != Infrastructure.SubmissionQueueStatus.FailedPermanent;
                TrackQueue(pending);
                submissionRecovery.Trigger();
            }
        }
        receiptReceived = state.LastReceipt is not null;
        RaiseCommands();
    }

    private async Task PickAsync()
    {
        var path = AppServices.Files.PickFile("Bài làm đã nén|*.zip;*.rar;*.7z");
        if (string.IsNullOrWhiteSpace(path)) return;
        var info = new FileInfo(path);
        SelectedPath = info.FullName;
        FileName = info.Name;
        FileType = Path.GetExtension(info.Name).TrimStart('.').ToUpperInvariant();
        FileSize = info.Exists ? FormatBytes(info.Length) : "-";
        Sha256 = "Chưa tính";
        IsFileValid = false;
        if (!info.Exists)
        {
            IsFileValid = false;
            ValidationStatus = "File không tồn tại";
        }
        else if (!StudentSubmissionPolicy.IsAllowedExtension(info.Name))
        {
            IsFileValid = false;
            ValidationStatus = "Bài làm phải được nén thành một file .zip, .rar hoặc .7z trước khi nộp.";
        }
        else if (info.Length <= 0 || info.Length > StudentSubmissionPolicy.MaxBytes)
        {
            IsFileValid = false;
            ValidationStatus = "File bài làm vượt quá 10 MB. Hãy xóa dữ liệu không cần thiết hoặc giảm dung lượng rồi nén lại.";
        }
        else
        {
            ValidationStatus = "Đang tính SHA-256";
            Sha256 = await Infrastructure.SubmissionQueueStore.HashFileAsync(info.FullName, DisposeToken);
            IsFileValid = true;
            ValidationStatus = "Hợp lệ · sẵn sàng lưu an toàn";
        }
    }

    internal Task SubmitAsync()
    {
        var decision = EvaluateEligibility();
        if (!decision.Allowed)
        {
            ApplyEligibilityDenial(decision);
            return Task.CompletedTask;
        }
        if (authState.CurrentAccount is null)
        {
            Status = "Hãy đăng nhập đúng tài khoản học sinh trước khi nộp bài.";
            StatusTone = "warning";
            return Task.CompletedTask;
        }

        return SubmitEligibleAsync();
    }

    private Task SubmitEligibleAsync()
    {
        var attachedToExisting = false;
        return RunAsync(
            "Đang sao chép bài vào vùng lưu an toàn",
            () => attachedToExisting
                ? "Bài nộp đang được xử lý; hệ thống tiếp tục dùng bản đã lưu trước đó"
                : "Đã lưu trên máy; hệ thống sẽ tự gửi và chỉ báo thành công sau khi có biên nhận",
            async ct =>
            {
                var prepared = await Infrastructure.SubmissionQueueStore.PrepareOrGetActiveAsync(
                    SelectedPath!, api.BaseAddress.ToString(), authState.CurrentAccount!.UserId, authState.CurrentAccount.StudentCode ?? state.StudentCode,
                    state.SessionId!.Value, state.ParticipantId!.Value, state.RoomCode, state.AccessMode, state.ServerId, state.AccessToken, ct);
                attachedToExisting = !prepared.Created;
                var queued = prepared.Submission;
                SelectedPath = queued.FilePath;
                Sha256 = queued.Sha256;
                TrackQueue(queued);
                if (prepared.Created)
                    submissionRecovery.Trigger();
            });
    }

    private SubmissionEligibilityDecision EvaluateEligibility() =>
        SubmissionEligibilityPolicy.Evaluate(new(
            IsBusy,
            state.HasSession,
            state.SessionId,
            state.ParticipantId,
            state.ParticipantStatus,
            state.SessionStatus,
            state.DeliveryType,
            IsFileValid
                && !string.IsNullOrWhiteSpace(SelectedPath)
                && File.Exists(SelectedPath),
            hasActiveQueue,
            receiptReceived || state.LastReceipt is not null,
            state.SubmissionStatus,
            state.ResubmitAllowed));

    private void ApplyEligibilityDenial(SubmissionEligibilityDecision decision)
    {
        ValidationStatus = decision.UserMessage;
        Status = decision.UserMessage;
        StatusTone = "warning";
        RaiseCommands();
    }

    internal void TrackQueue(Infrastructure.PendingSubmission item)
    {
        trackedQueueSessionId = item.SessionId;
        trackedQueueParticipantId = item.ParticipantId;
        QueueId = item.QueueId;
        EnsureProgressSubscription();
        ApplyProgressSnapshot(Infrastructure.SubmissionQueueStore.CreateProgressSnapshot(item));
    }

    private void OnSessionStatePropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if ((args.PropertyName is nameof(StudentSessionState.SessionId)
                or nameof(StudentSessionState.ParticipantId))
            && (trackedQueueSessionId != state.SessionId
                || trackedQueueParticipantId != state.ParticipantId))
        {
            hasActiveQueue = false;
            trackedQueueSessionId = null;
            trackedQueueParticipantId = null;
            QueueId = null;
            StopProgressSubscription();
        }

        if (args.PropertyName == nameof(StudentSessionState.LastReceipt))
            receiptReceived = state.LastReceipt is not null;

        if (args.PropertyName is nameof(StudentSessionState.SessionId)
            or nameof(StudentSessionState.ParticipantId)
            or nameof(StudentSessionState.ParticipantStatus)
            or nameof(StudentSessionState.SessionStatus)
            or nameof(StudentSessionState.DeliveryType)
            or nameof(StudentSessionState.SubmissionStatus)
            or nameof(StudentSessionState.LastReceipt)
            or nameof(StudentSessionState.ResubmitAllowed))
            RaiseCommands();
    }

    private void EnsureProgressSubscription()
    {
        if (progressSubscribed || IsDisposed) return;
        submissionRecovery.ProgressChanged += OnProgressChanged;
        progressSubscribed = true;
    }

    private void StopProgressSubscription()
    {
        if (!progressSubscribed) return;
        submissionRecovery.ProgressChanged -= OnProgressChanged;
        progressSubscribed = false;
    }

    private void OnProgressChanged(
        object? sender,
        Infrastructure.SubmissionProgressSnapshot snapshot)
    {
        if (IsDisposed || snapshot.QueueId != QueueId) return;

        void ApplySafely()
        {
            if (IsDisposed || snapshot.QueueId != QueueId) return;
            try
            {
                ApplyProgressSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                FrontendLogger.Log(ex, "StudentSubmission.ProgressChanged");
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher?.CheckAccess() == false)
            _ = dispatcher.BeginInvoke(ApplySafely);
        else
            ApplySafely();
    }

    private void ApplyProgressSnapshot(
        Infrastructure.SubmissionProgressSnapshot snapshot)
    {
        Progress = snapshot.ProgressPercent;
        hasActiveQueue = !snapshot.IsTerminal;
        receiptReceived = receiptReceived || snapshot.ReceiptReceived;
        QueueError = snapshot.LastError;
        var statusText = snapshot.Status == Infrastructure.SubmissionQueueStatus.Completed
            && !snapshot.ReceiptReceived
            ? QueueStatusText(Infrastructure.SubmissionQueueStatus.AwaitingReceipt)
            : QueueStatusText(snapshot.Status);
        ValidationStatus = string.IsNullOrWhiteSpace(snapshot.LastError)
            ? statusText
            : snapshot.LastError;
        Status = string.IsNullOrWhiteSpace(snapshot.LastError)
            ? $"Trạng thái bài nộp: {statusText}"
            : $"Bài nộp cần xử lý: {snapshot.LastError}";
        StatusTone = snapshot.IsCompleted
            ? "success"
            : snapshot.IsTerminal || !string.IsNullOrWhiteSpace(snapshot.LastError)
                ? "danger"
                : "info";
        RaiseCommands();
        if (snapshot.IsTerminal)
            StopProgressSubscription();
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:N2} MB";
    private static string QueueStatusText(Infrastructure.SubmissionQueueStatus status) => status switch
    {
        Infrastructure.SubmissionQueueStatus.Prepared => "Đã lưu trên máy",
        Infrastructure.SubmissionQueueStatus.WaitingForConnection => "Đang chờ kết nối",
        Infrastructure.SubmissionQueueStatus.Initializing or Infrastructure.SubmissionQueueStatus.Uploading => "Đang gửi tiếp",
        Infrastructure.SubmissionQueueStatus.Finalizing => "Đang xác nhận",
        Infrastructure.SubmissionQueueStatus.AwaitingReceipt => "Máy chủ đã nhận, đang chờ biên nhận",
        Infrastructure.SubmissionQueueStatus.Completed => "Đã có biên nhận",
        Infrastructure.SubmissionQueueStatus.NeedsLogin => "Cần đăng nhập lại",
        Infrastructure.SubmissionQueueStatus.NeedsRejoin => "Cần giáo viên duyệt lại",
        Infrastructure.SubmissionQueueStatus.Expired => "Đã quá thời hạn",
        _ => "File bị lỗi"
    };

    protected override void RaiseCommands()
    {
        (PickCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SubmitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        state.PropertyChanged -= OnSessionStatePropertyChanged;
        StopProgressSubscription();
        base.Dispose();
    }
}

public sealed class StudentReceiptViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private ReceiptDto? receipt;

    public StudentReceiptViewModel(IBackendClient api, StudentSessionState state)
    {
        this.api = api;
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && state.LastSubmissionId.HasValue);
        CopyCommand = new RelayCommand(Copy);
        SaveCommand = new RelayCommand(Save);
    }

    public ReceiptDto? Receipt { get => receipt; private set => Set(ref receipt, value); }
    public ICommand RefreshCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SaveCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (state.LastReceipt is not null)
        {
            Receipt = state.LastReceipt;
            Status = "Biên nhận đã được xác minh";
            StatusTone = "success";
            return;
        }
        if (!state.LastSubmissionId.HasValue)
        {
            Status = "Chưa có bài nộp được máy chủ xác nhận";
            StatusTone = "warning";
            return;
        }
        await RunAsync("Đang tải biên nhận", "Biên nhận đã được xác minh", async token =>
        {
            api.SetParticipantToken(state.AccessToken);
            Receipt = ApiGuard.Require(await api.GetAsync<ReceiptDto>($"api/v1/submissions/{state.LastSubmissionId}/receipt", token));
            state.LastReceipt = Receipt;
        });
    }

    private void Copy()
    {
        if (Receipt is null) return;
        AppServices.Clipboard.SetText(Receipt.ReceiptCode);
        Status = "Mã biên nhận đã được sao chép";
        StatusTone = "success";
    }

    private void Save()
    {
        if (Receipt is null) return;
        var folder = AppServices.Folders.PickFolder();
        if (folder is null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(Receipt, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(folder, $"receipt-{Receipt.ReceiptCode}.json"), json);
        Status = "Biên nhận JSON đã được lưu";
        StatusTone = "success";
    }

    protected override void RaiseCommands() => (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
}

public sealed class StudentHistoryViewModel : ProductPageBase
{
    private readonly StudentSessionState state;

    public StudentHistoryViewModel(StudentSessionState state)
    {
        this.state = state;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
    }

    public ObservableCollection<StudentHistoryRow> History { get; } = new();
    public ICommand RefreshCommand { get; }

    protected override Task LoadAsync(CancellationToken ct) => RunAsync("Đang tải lịch sử trên máy", "Lịch sử cục bộ đã được cập nhật", token =>
    {
        History.Clear();
        if (state.LastReceipt is not null)
        {
            History.Add(new(state.RoomCode, state.LastReceipt.ReceiptCode, state.LastReceipt.ServerReceivedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), state.LastReceipt.IsLate ? "Nộp muộn" : "Đúng hạn"));
        }
        return Task.CompletedTask;
    });

    protected override void RaiseCommands() => (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
}

public sealed record StudentHistoryRow(string RoomCode, string ReceiptCode, string SubmittedAt, string Status);

public sealed class StudentSettingsViewModel : ProductPageBase
{
    private readonly ILocalPreferenceService preferences;
    private string displayName = string.Empty;
    private string studentCode = string.Empty;
    private string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ExamTransfer", "Working");
    private bool notifications = true;

    public StudentSettingsViewModel(ILocalPreferenceService preferences)
    {
        this.preferences = preferences;
        SaveCommand = new RelayCommand(Save);
        BrowseCommand = new RelayCommand(Browse);
    }

    public string DisplayName { get => displayName; private set => Set(ref displayName, value); }
    public string StudentCode { get => studentCode; private set => Set(ref studentCode, value); }
    public string Workspace { get => workspace; set => Set(ref workspace, value); }
    public bool Notifications { get => notifications; set => Set(ref notifications, value); }
    public ICommand SaveCommand { get; }
    public ICommand BrowseCommand { get; }

    protected override Task LoadAsync(CancellationToken ct)
    {
        DisplayName = preferences.Get("student-name") ?? string.Empty;
        StudentCode = preferences.Get("student-code") ?? string.Empty;
        Workspace = preferences.Get("workspace") ?? Workspace;
        Notifications = !string.Equals(
            preferences.Get("notifications"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        Status = "Thông tin tài khoản và tùy chọn đã được tải";
        StatusTone = "success";
        return Task.CompletedTask;
    }

    private void Save()
    {
        preferences.Set("workspace", Workspace);
        preferences.Set("notifications", Notifications ? "true" : "false");
        Status = "Tùy chọn học sinh đã được lưu";
        StatusTone = "success";
    }

    private void Browse()
    {
        var folder = AppServices.Folders.PickFolder();
        if (folder is not null) Workspace = folder;
    }

}

internal static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
