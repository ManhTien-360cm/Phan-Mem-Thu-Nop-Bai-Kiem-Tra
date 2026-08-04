using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentExamViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly IStudentHeartbeatService heartbeat;
    private readonly IStudentRealtimeService realtime;
    private readonly IServerClock serverClock;
    private readonly ServerTimelineCoordinator timelineCoordinator;
    private readonly ICountdownTicker ticker;
    private FileSystemWatcher? watcher;
    private SessionDetailDto? session;
    private ParticipantDto? participant;
    private TimeSpan? remaining;
    private DateTimeOffset? publicDeadlineUtc;
    private DateTimeOffset? publicStartedAtUtc;
    private string? publicSessionStatus;
    private int snapshotResyncRequested;
    private string connection = "Chưa kết nối phiên";
    private string workspaceFolder;

    public StudentExamViewModel(IBackendClient api, StudentSessionState state)
        : this(
            api,
            state,
            AppServices.StudentHeartbeat,
            AppServices.StudentRealtime,
            AppServices.ServerClock,
            AppServices.CountdownTickers.Create(TimeSpan.FromSeconds(1)))
    {
    }

    public StudentExamViewModel(
        IBackendClient api,
        StudentSessionState state,
        IStudentHeartbeatService heartbeat,
        IStudentRealtimeService realtime,
        IServerClock serverClock,
        ICountdownTicker ticker)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        this.realtime = realtime ?? throw new ArgumentNullException(nameof(realtime));
        this.serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock));
        timelineCoordinator = new ServerTimelineCoordinator(this.serverClock);
        this.ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        heartbeat.StateChanged += OnHeartbeatStateChanged;
        realtime.EventReceived += OnRealtimeEvent;
        realtime.NotificationReceived += OnRealtimeNotification;
        workspaceFolder = AppServices.Preferences.Get("exam.workspace")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ExamTransfer", "Working");

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && state.HasSession);
        ContinueExamCommand = new AsyncRelayCommand(ContinueExamAsync, () => !IsBusy);
        HeartbeatCommand = new AsyncRelayCommand(HeartbeatAsync, () => !IsBusy && state.HasSession);
        BrowseWorkspaceCommand = new RelayCommand(BrowseWorkspace);
        OpenWorkspaceCommand = new RelayCommand(OpenWorkspace);
        RefreshWorkspaceCommand = new AsyncRelayCommand(LoadWorkspaceAsync, () => !IsBusy);

        ticker.Tick += OnTick;
        if (IsTerminal(state.SessionStatus))
        {
            remaining = TimeSpan.Zero;
            ticker.Stop();
        }
        else
        {
            ticker.Start();
        }
    }

    public ObservableCollection<ExamStep> Steps { get; } = new()
    {
        new(1, "Xác nhận tham gia", "Chưa thực hiện", false, false),
        new(2, "Nhận đề", "Chưa thực hiện", false, false),
        new(3, "Làm bài trong thư mục", "Chưa thực hiện", false, false),
        new(4, "Nộp bài", "Chưa thực hiện", false, false),
        new(5, "Biên nhận", "Chưa thực hiện", false, false)
    };

    public ObservableCollection<StudentMessage> Messages { get; } = new();
    public ObservableCollection<WorkspaceFileRow> WorkspaceFiles { get; } = new();
    public string Title => Session?.Summary.Title
        ?? (state.AccessMode == SessionAccessMode.PublicCloud
            ? "Kỳ thi PublicCloud hiện tại"
            : "Chưa có kỳ thi đang hoạt động");
    public string Subject => state.ExamId.HasValue ? $"Mã đề {state.ExamId.Value.ToString("N")[..8].ToUpperInvariant()}" : "";
    public string Teacher => "Máy chủ phòng thi";
    public string RoomCode => state.RoomCode;
    public string CandidateCount => Session is null
        ? (state.AccessMode == SessionAccessMode.PublicCloud ? "1" : "0")
        : Session.Participants.Count.ToString();
    public string TimeLeft => ServerCountdown.Format(remaining);
    private DateTimeOffset? EffectiveDeadlineUtc => state.AccessMode == SessionAccessMode.PublicCloud
        ? publicDeadlineUtc
        : Participant?.EffectiveDeadlineUtc ?? Session?.Summary.EffectiveDeadlineUtc;
    private DateTimeOffset? EffectiveStartUtc => state.AccessMode == SessionAccessMode.PublicCloud
        ? publicStartedAtUtc
        : Session?.Summary.StartTimeUtc;
    public double TimeProgress => EffectiveStartUtc is null || EffectiveDeadlineUtc is null || remaining is null
        ? 0
        : Math.Clamp(remaining.Value.TotalSeconds / Math.Max(1, (EffectiveDeadlineUtc.Value - EffectiveStartUtc.Value).TotalSeconds) * 100, 0, 100);
    public SessionDetailDto? Session { get => session; private set { if (Set(ref session, value)) { Raise(nameof(Title)); Raise(nameof(Subject)); Raise(nameof(RoomCode)); Raise(nameof(CandidateCount)); } } }
    public ParticipantDto? Participant { get => participant; private set => Set(ref participant, value); }
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public string WorkspaceFolder { get => workspaceFolder; set { if (Set(ref workspaceFolder, value)) AppServices.Preferences.Set("exam.workspace", value); } }
    public ICommand RefreshCommand { get; }
    public ICommand ContinueExamCommand { get; }
    public ICommand HeartbeatCommand { get; }
    public ICommand BrowseWorkspaceCommand { get; }
    public ICommand OpenWorkspaceCommand { get; }
    public ICommand RefreshWorkspaceCommand { get; }

    private async Task ContinueExamAsync()
    {
        var resolution = await AppServices.StudentExamFlow.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            DisposeToken);
        if (!resolution.RequiresStartConfirmation)
            return;
        if (!AppServices.Dialogs.Confirm(
                "Bắt đầu bài trắc nghiệm",
                "Sau khi xác nhận, máy chủ sẽ tạo hoặc tiếp tục đúng một lượt làm bài. Bắt đầu ngay?"))
            return;
        _ = await AppServices.StudentExamFlow.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            true,
            DisposeToken);
    }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await LoadWorkspaceAsync();
        if (!state.HasSession)
        {
            Connection = "Chưa có phiên thi hợp lệ";
            Status = "Hãy kết nối phòng, xác thực thông tin và được giáo viên duyệt trước.";
            StatusTone = "warning";
            return;
        }

        await RunAsync("Đang đồng bộ kỳ thi", "Thông tin kỳ thi đã được cập nhật", async token =>
        {
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                var timeline = await AppServices.PublicCloud.GetStudentTimelineAsync(
                    state.SessionId!.Value,
                    token);
                if (timeline.ParticipantId != state.ParticipantId)
                    throw new InvalidDataException("PublicCloud timeline không thuộc thí sinh hiện tại.");
                _ = ApplyPublicTimeline(timeline);
                Connection = $"Đã xác thực PublicCloud · {state.SessionStatus}";
                UpdateSteps();
                RaiseTime();
                return;
            }

            api.SetParticipantToken(state.AccessToken);
            var loadedSession = ApiGuard.Require(await api.GetSessionAsync(state.SessionId!.Value, token));
            var loadedParticipant = ApiGuard.Require(await api.GetAsync<ParticipantDto>(
                $"api/v1/sessions/{state.SessionId}/participants/{state.ParticipantId}", token));
            if (!TryApplyLifecycleSnapshot(
                    loadedSession.Summary.Status,
                    loadedSession.Summary.Sequence,
                    loadedSession.Summary.StartTimeUtc,
                    loadedParticipant.EffectiveDeadlineUtc
                        ?? loadedSession.Summary.EffectiveDeadlineUtc,
                    loadedSession.Summary.ServerNowUtc))
                return;
            Session = loadedSession;
            Participant = loadedParticipant;
            state.ExamId = loadedSession.Summary.ExamId;
            state.ParticipantStatus = loadedParticipant.Status;
            state.SubmissionStatus = loadedParticipant.SubmissionStatus;
            state.ApplyResubmitAuthority(loadedParticipant.ResubmitAllowed);
            Connection = $"Đã xác thực · {Session.Summary.Status}";
            UpdateSteps();
            RaiseTime();
        });
    }

    private Task HeartbeatAsync() => RunAsync("Đang kiểm tra kết nối", "Kết nối phòng thi ổn định", async ct =>
    {
        if (!state.SessionId.HasValue || !state.ParticipantId.HasValue) return;
        if (!await heartbeat.ProbeNowAsync(ct))
            throw new InvalidOperationException("Máy chủ chưa phản hồi; vòng kết nối nền sẽ tự thử lại.");
        Connection = "Đã kết nối máy chủ";
    });

    private void OnHeartbeatStateChanged(object? sender, StudentConnectionState value)
    {
        if (IsDisposed) return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Connection = value switch
        {
            StudentConnectionState.Online => "Đã kết nối máy chủ",
            StudentConnectionState.Connecting => "Đang kết nối máy chủ",
            StudentConnectionState.Reconnecting => "Mất kết nối tạm thời · đang thử lại",
            StudentConnectionState.Offline => "Ngoại tuyến · vẫn giữ phiên thi",
            StudentConnectionState.AuthenticationExpired => "Token phiên thi đã hết hạn",
            _ => "Chưa kết nối phiên"
        });
    }

    private void OnRealtimeEvent(object? sender, string eventName)
    {
        if (IsDisposed) return;
        if (eventName is RealtimeEvents.TimeExtended or "Reconnected")
        {
            RequestSnapshotResync();
            return;
        }
        if (state.AccessMode != SessionAccessMode.PublicCloud
            && (eventName is RealtimeEvents.SessionStateChanged
                or RealtimeEvents.ParticipantApproved))
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => LoadAsync(DisposeToken).SafeFireAndForget("StudentExam.RealtimeRefresh"));
    }

    private void OnRealtimeNotification(object? sender, StudentRealtimeNotification notification)
    {
        if (IsDisposed)
            return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => TryApplyTimeExtended(notification));
    }

    public bool TryApplyTimeExtended(StudentRealtimeNotification notification)
    {
        var payload = notification.TimeExtended;
        if (IsDisposed
            || IsTerminal(state.SessionStatus)
            || notification.EventName != RealtimeEvents.TimeExtended
            || notification.SessionId != state.SessionId
            || payload is null
            || payload.ParticipantId != state.ParticipantId
            || !payload.ServerNowUtc.HasValue
            || !payload.Revision.HasValue)
            return false;
        if (!timelineCoordinator.TryApply(
                payload.Revision.Value,
                payload.EffectiveDeadlineUtc,
                payload.ServerNowUtc.Value))
            return false;
        if (state.AccessMode == SessionAccessMode.PublicCloud)
            publicDeadlineUtc = payload.EffectiveDeadlineUtc;
        else if (Participant is not null)
            Participant = Participant with { EffectiveDeadlineUtc = payload.EffectiveDeadlineUtc };
        UpdateRemaining();
        RaiseTime();
        UpdateSteps();
        return true;
    }

    private bool ApplyPublicTimeline(PublicStudentTimeline timeline)
    {
        if (!timeline.EffectiveDeadlineUtc.HasValue)
            throw new InvalidDataException("PublicCloud chưa trả deadline tuyệt đối.");
        if (!Enum.TryParse<SessionStatus>(
                timeline.SessionStatus,
                true,
                out var sessionStatus))
            throw new InvalidDataException("PublicCloud trả trạng thái phiên không hợp lệ.");
        if (!TryApplyLifecycleSnapshot(
                sessionStatus,
                timeline.Revision,
                timeline.StartedAtUtc,
                timeline.EffectiveDeadlineUtc,
                timeline.ServerNowUtc))
            return false;
        state.ApplyResubmitAuthority(timeline.ResubmitAllowed);
        Raise(nameof(Title));
        Raise(nameof(CandidateCount));
        return true;
    }

    internal bool TryApplyLifecycleSnapshot(
        SessionStatus sessionStatus,
        long revision,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? deadlineUtc,
        DateTimeOffset serverNowUtc)
    {
        if (revision < state.Revision
            || (revision == state.Revision
                && IsTerminal(state.SessionStatus)
                && !IsTerminal(sessionStatus)))
            return false;

        if (IsTerminal(sessionStatus))
        {
            serverClock.Synchronize(serverNowUtc);
            state.SessionStatus = sessionStatus;
            state.Revision = revision;
            if (state.AccessMode == SessionAccessMode.PublicCloud)
            {
                publicStartedAtUtc = startedAtUtc;
                publicDeadlineUtc = deadlineUtc;
                publicSessionStatus = sessionStatus.ToString();
            }
            remaining = TimeSpan.Zero;
            ticker.Stop();
            RaiseTime();
            RaiseCommands();
            return true;
        }

        if (deadlineUtc.HasValue)
        {
            if (!timelineCoordinator.TryApply(
                    revision,
                    deadlineUtc.Value,
                    serverNowUtc))
                return false;
        }
        else
        {
            serverClock.Synchronize(serverNowUtc);
        }

        state.SessionStatus = sessionStatus;
        state.Revision = revision;
        if (state.AccessMode == SessionAccessMode.PublicCloud)
        {
            publicStartedAtUtc = startedAtUtc;
            publicDeadlineUtc = deadlineUtc;
            publicSessionStatus = sessionStatus.ToString();
        }
        remaining = ServerCountdown.Remaining(serverClock, deadlineUtc);
        if (!ticker.IsRunning)
            ticker.Start();
        RaiseTime();
        RaiseCommands();
        return true;
    }

    private void RequestSnapshotResync()
    {
        if (Interlocked.Exchange(ref snapshotResyncRequested, 1) != 0)
            return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { await LoadAsync(DisposeToken); }
            finally { Interlocked.Exchange(ref snapshotResyncRequested, 0); }
        });
    }

    private async Task LoadWorkspaceAsync()
    {
        try
        {
            Directory.CreateDirectory(WorkspaceFolder);
            var rows = Directory.EnumerateFiles(WorkspaceFolder, "*", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new WorkspaceFileRow(info.Name, FormatBytes(info.Length), info.LastWriteTime.ToString("HH:mm dd/MM/yyyy"), info.Length <= 200L * 1024 * 1024 ? "Hợp lệ" : "Vượt giới hạn");
                }).ToArray();
            WorkspaceFiles.ReplaceWith(rows);
            StartWorkspaceWatcher();
            UpdateSteps();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "StudentExam.Workspace");
            Status = "Không thể đọc thư mục làm bài. Hãy chọn thư mục khác.";
            StatusTone = "danger";
        }
    }

    private void BrowseWorkspace()
    {
        var selected = AppServices.Folders.PickFolder();
        if (string.IsNullOrWhiteSpace(selected)) return;
        WorkspaceFolder = selected;
        LoadWorkspaceAsync().SafeFireAndForget("StudentExam.BrowseWorkspace");
    }

    private void OpenWorkspace()
    {
        Directory.CreateDirectory(WorkspaceFolder);
        Process.Start(new ProcessStartInfo(WorkspaceFolder) { UseShellExecute = true });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (IsTerminal(state.SessionStatus))
        {
            remaining = TimeSpan.Zero;
            ticker.Stop();
            RaiseTime();
            return;
        }
        UpdateRemaining();
        RaiseTime();
        UpdateSteps();
    }

    private void UpdateRemaining() =>
        remaining = ServerCountdown.Remaining(serverClock, EffectiveDeadlineUtc);

    private static bool IsTerminal(SessionStatus? status) =>
        status is SessionStatus.Finished or SessionStatus.Cancelled or SessionStatus.Archived;

    private void StartWorkspaceWatcher()
    {
        watcher?.Dispose();
        watcher = new FileSystemWatcher(WorkspaceFolder)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Created += OnWorkspaceChanged;
        watcher.Changed += OnWorkspaceChanged;
        watcher.Deleted += OnWorkspaceChanged;
        watcher.Renamed += OnWorkspaceChanged;
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        if (IsDisposed) return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            LoadWorkspaceAsync().SafeFireAndForget("StudentExam.WorkspaceWatcher"));
    }

    private void RaiseTime()
    {
        Raise(nameof(TimeLeft));
        Raise(nameof(TimeProgress));
    }

    private void UpdateSteps()
    {
        if (Session is null && state.AccessMode != SessionAccessMode.PublicCloud) return;
        var active = state.AccessMode == SessionAccessMode.PublicCloud
            ? publicSessionStatus ?? string.Empty
            : Session!.Summary.Status.ToString();
        Steps[0] = Steps[0] with { Status = "Đã xác nhận", Completed = true, Active = false };
        Steps[1] = Steps[1] with { Status = state.ExamId.HasValue ? "Sẵn sàng nhận" : "Chờ phát đề", Completed = state.ExamId.HasValue, Active = false };
        Steps[2] = Steps[2] with { Status = WorkspaceFiles.Count > 0 ? $"{WorkspaceFiles.Count} file" : "Chưa có file", Completed = false, Active = active is "InProgress" or "Paused" };
        Steps[3] = Steps[3] with
        {
            Status = remaining is null ? "Chưa đồng bộ giờ" : remaining > TimeSpan.Zero ? "Có thể nộp" : "Đã hết giờ",
            Completed = state.LastSubmissionId.HasValue,
            Active = false
        };
        Steps[4] = Steps[4] with { Status = state.LastReceipt is null ? "Chưa có" : "Đã nhận", Completed = state.LastReceipt is not null, Active = false };
    }

    private static string FormatBytes(long value) => value < 1024 * 1024 ? $"{value / 1024d:N1} KB" : $"{value / 1024d / 1024d:N1} MB";

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, HeartbeatCommand, RefreshWorkspaceCommand }.OfType<AsyncRelayCommand>())
            command.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        ticker.Tick -= OnTick;
        ticker.Dispose();
        watcher?.Dispose();
        heartbeat.StateChanged -= OnHeartbeatStateChanged;
        realtime.EventReceived -= OnRealtimeEvent;
        realtime.NotificationReceived -= OnRealtimeNotification;
        base.Dispose();
    }
}

public sealed record ExamStep(int Number, string Title, string Status, bool Completed, bool Active);
public sealed record StudentMessage(string Title, string Description, string Time);
