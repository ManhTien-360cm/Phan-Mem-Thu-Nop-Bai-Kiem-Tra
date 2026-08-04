using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class LiveMonitorViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IRealtimeService realtime;
    private readonly TeacherRealtimeSessionBinding realtimeBinding;
    private readonly ProjectionRefreshCoordinator projectionRefresh;
    private readonly RealtimeRefreshDebouncer realtimeRefresh =
        new(TimeSpan.FromMilliseconds(150), "LiveMonitor.RealtimeRefresh");
    private CancellationTokenSource? cloudPollingCts;
    private SessionSummaryDto? selectedSession;
    private ParticipantDto? selectedParticipant;
    private string message = "Vui lòng kiểm tra file bài làm trước khi nộp.";
    private string extraMinutes = "5";

    public LiveMonitorViewModel(
        IBackendClient api,
        IRealtimeService? realtime = null)
    {
        this.api = api;
        this.realtime = realtime ?? new RealtimeService(AppServices.BaseUrl);
        realtimeBinding = new TeacherRealtimeSessionBinding(this.realtime);
        projectionRefresh = new ProjectionRefreshCoordinator(
            RefreshProjectionSnapshotAsync,
            TimeSpan.FromMilliseconds(150),
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)],
            [TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(850)]);
        this.realtime.NotificationReceived += OnRealtimeNotification;
        this.realtime.EventReceived += OnRealtimeEvent;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy);
        LoadCommand = new AsyncRelayCommand(LoadSessionAsync, () => !IsBusy && SelectedSession is not null);
        MessageCommand = new AsyncRelayCommand(MessageAsync, () => !IsBusy && SelectedSession is not null);
        ExtraTimeCommand = new AsyncRelayCommand(ExtraTimeAsync, () => !IsBusy && SelectedSession is not null && SelectedParticipant is not null);
        PauseCommand = new AsyncRelayCommand(() => TransitionAsync("pause", "Phiên thi đã tạm dừng"), () => !IsBusy && SelectedSession is not null);
        ResumeCommand = new AsyncRelayCommand(() => TransitionAsync("resume", "Phiên thi đã tiếp tục"), () => !IsBusy && SelectedSession is not null);
        CollectCommand = new AsyncRelayCommand(() => TransitionAsync("collect", "Đã chuyển sang giai đoạn thu bài"), () => !IsBusy && SelectedSession is not null);
        EndCommand = new AsyncRelayCommand(EndAsync, () => !IsBusy && SelectedSession is not null);
    }

    public ObservableCollection<SessionSummaryDto> Sessions { get; } = new();
    public ObservableCollection<ParticipantDto> Participants { get; } = new();
    public ObservableCollection<MonitorEvent> Events { get; } = new();
    public SessionSummaryDto? SelectedSession
    {
        get => selectedSession;
        set
        {
            if (!Set(ref selectedSession, value))
                return;
            realtimeBinding
                .SelectAsync(value?.Id, DisposeToken)
                .SafeFireAndForget("LiveMonitor.SelectRealtimeSession");
            StopCloudPolling();
            RaiseCommands();
        }
    }
    public ParticipantDto? SelectedParticipant { get => selectedParticipant; set { if (Set(ref selectedParticipant, value)) RaiseCommands(); } }
    public string Message { get => message; set => Set(ref message, value); }
    public string ExtraMinutes { get => extraMinutes; set => Set(ref extraMinutes, value); }
    public ICommand RefreshCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand MessageCommand { get; }
    public ICommand ExtraTimeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CollectCommand { get; }
    public ICommand EndCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await RunAsync("Đang tải các phiên trực tiếp", "Giám sát trực tiếp đã được cập nhật", async token =>
        {
            var data = ApiGuard.Require(await api.GetSessionsAsync(token));
            Sessions.ReplaceWith(data.Items.Where(x => x.Status is SessionStatus.Waiting or SessionStatus.Distributing or SessionStatus.InProgress or SessionStatus.Paused or SessionStatus.Collecting));
            SelectedSession ??= Sessions.FirstOrDefault();
            await realtimeBinding.EnsureAsync(
                AppServices.AuthState.AccountAccessToken,
                SelectedSession?.Id,
                token);
            if (SelectedSession is not null)
            {
                await LoadSessionCoreAsync(token);
                if (SelectedSession.AccessMode == SessionAccessMode.PublicCloud)
                {
                    projectionRefresh.StartRecovery();
                    StartCloudPolling();
                }
            }
        });
    }

    private Task LoadSessionAsync() => RunAsync("Đang đồng bộ trạng thái học sinh", "Trạng thái học sinh đã được cập nhật", LoadSessionCoreAsync);
    private async Task LoadSessionCoreAsync(CancellationToken ct)
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.GetSessionAsync(SelectedSession.Id, ct));
        Participants.ReplaceWith(detail.Participants);
        SelectedParticipant = Participants.FirstOrDefault();
        Events.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), "Đồng bộ snapshot", $"Đã nhận {Participants.Count} trạng thái thiết bị", "primary", "\uE72C"));
    }

    private Task MessageAsync() => RunAsync("Đang gửi thông báo", "Thông báo đã được gửi", async ct =>
    {
        if (SelectedSession is null) return;
        _ = ApiGuard.Require(await api.PostAsync<SendMessageRequest, MessageDto>($"api/v1/sessions/{SelectedSession.Id}/messages", new(SelectedParticipant?.Id, MessageType.Information, Message), ct));
        Events.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), "Gửi thông báo", Message, "info", "\uE8BD"));
    });

    private Task ExtraTimeAsync() => RunAsync("Đang cộng thời gian", "Thời gian bổ sung đã được áp dụng", async ct =>
    {
        if (SelectedSession is null || SelectedParticipant is null || !int.TryParse(ExtraMinutes, out var minutes)) return;
        var mutationKey = $"extra-time:{SelectedSession.Id:N}:{SelectedParticipant.Id:N}:{minutes}";
        var mutationId = GetMutationRequestId(mutationKey);
        var updated = ApiGuard.Require(await api.PostAsync<ExtraTimeRequest, ParticipantDto>($"api/v1/sessions/{SelectedSession.Id}/participants/{SelectedParticipant.Id}/extra-time", new(minutes, "Giáo viên điều chỉnh thời gian.", mutationId), ct));
        CompleteMutationRequest(mutationKey);
        ReplaceParticipant(updated);
    });

    private Task TransitionAsync(string action, string success) => RunAsync("Đang cập nhật phiên", success, async ct =>
    {
        if (SelectedSession is null) return;
        var detail = ApiGuard.Require(await api.PostAsync<object, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/{action}", new { }, ct));
        SelectedSession = detail.Summary;
    });

    private Task EndAsync() => RunAsync("Đang kết thúc phiên", "Phiên thi đã kết thúc", async ct =>
    {
        if (SelectedSession is null) return;
        var uploading = Participants.Count(x => x.SubmissionStatus == SubmissionStatus.Uploading);
        var missing = Participants.Count(x => x.SubmissionStatus == SubmissionStatus.NotStarted);
        if (!AppServices.Dialogs.Confirm("Kết thúc phiên", $"Còn {uploading} bài đang tải và {missing} học sinh chưa nộp. Vẫn kết thúc?")) return;
        _ = ApiGuard.Require(await api.PostAsync<EndSessionRequest, SessionDetailDto>($"api/v1/sessions/{SelectedSession.Id}/end", new(true, "Giáo viên xác nhận."), ct));
    });

    private void ReplaceParticipant(ParticipantDto updated)
    {
        var old = Participants.FirstOrDefault(x => x.Id == updated.Id);
        if (old is not null) Participants[Participants.IndexOf(old)] = updated;
        SelectedParticipant = updated;
    }


    private void OnRealtimeNotification(
        object? sender,
        StudentRealtimeNotification notification)
    {
        if (IsDisposed
            || SelectedSession is not { } session
            || notification.SessionId != session.Id)
            return;

        if (notification.EventName == RealtimeEvents.PublicCloudProjectionUpdated)
        {
            void ScheduleProjectionRefresh()
            {
                var update = notification.ProjectionUpdated;
                if (session.AccessMode != SessionAccessMode.PublicCloud
                    || update is null
                    || update.SessionId != session.Id
                    || update.EntityType != PublicCloudProjectionEntityTypes.SessionParticipant
                    || !projectionRefresh.Schedule(session.Id, update.ProjectionVersion))
                    return;
                AddRealtimeEvent(notification.EventName);
            }
            var projectionDispatcher = Application.Current?.Dispatcher;
            if (projectionDispatcher is null || projectionDispatcher.CheckAccess())
                ScheduleProjectionRefresh();
            else
                projectionDispatcher.InvokeAsync(ScheduleProjectionRefresh);
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        void ScheduleRefresh()
        {
            AddRealtimeEvent(notification.EventName);
            realtimeRefresh.Schedule(LoadSessionAsync);
        }

        if (dispatcher is null || dispatcher.CheckAccess())
            ScheduleRefresh();
        else
            dispatcher.InvokeAsync(ScheduleRefresh);
    }

    private async Task RefreshProjectionSnapshotAsync(
        Guid? expectedSessionId,
        CancellationToken cancellationToken)
    {
        if (IsDisposed
            || SelectedSession is not { AccessMode: SessionAccessMode.PublicCloud } session
            || expectedSessionId.HasValue && expectedSessionId.Value != session.Id)
            return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            await LoadSessionCoreAsync(cancellationToken);
        else
            await dispatcher.InvokeAsync(
                () => LoadSessionCoreAsync(cancellationToken)).Task.Unwrap();
    }

    private void OnRealtimeEvent(object? sender, string eventName)
    {
        if (eventName != "Reconnected" || IsDisposed)
            return;
        var dispatcher = Application.Current?.Dispatcher;
        void Recover()
        {
            if (SelectedSession?.AccessMode == SessionAccessMode.PublicCloud)
                projectionRefresh.StartRecovery();
        }
        if (dispatcher is null || dispatcher.CheckAccess())
            Recover();
        else
            dispatcher.InvokeAsync(Recover);
    }

    private void AddRealtimeEvent(string eventName) =>
        Events.Insert(0, new MonitorEvent(
            DateTime.Now.ToString("HH:mm:ss"),
            "Cập nhật thời gian thực",
            eventName,
            "info",
            "\uE7BA"));

    private void StartCloudPolling()
    {
        StopCloudPolling();
        if (IsDisposed) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(DisposeToken);
        cloudPollingCts = cts;
        CloudPollingLoopAsync(cts.Token).SafeFireAndForget("LiveMonitor.CloudPolling");
    }

    private void StopCloudPolling()
    {
        var cts = cloudPollingCts;
        cloudPollingCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task CloudPollingLoopAsync(CancellationToken cancellationToken)
    {
        // Polling định kỳ 5 giây để giáo viên luôn thấy học sinh mới tham gia qua PublicCloud,
        // bù đắp cho trường hợp realtime event bị missed (vd: teacher mở màn sau khi student đã join).
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (cancellationToken.IsCancellationRequested) break;
                if (SelectedSession?.AccessMode != SessionAccessMode.PublicCloud) break;
                await RefreshProjectionSnapshotAsync(SelectedSession?.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;
        StopCloudPolling();
        realtime.NotificationReceived -= OnRealtimeNotification;
        realtime.EventReceived -= OnRealtimeEvent;
        realtimeRefresh.Dispose();
        projectionRefresh.Dispose();
        realtimeBinding
            .StopAsync()
            .SafeFireAndForget("LiveMonitor.DisconnectRealtime");
        base.Dispose();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, LoadCommand, MessageCommand, ExtraTimeCommand, PauseCommand, ResumeCommand, CollectCommand, EndCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }
}

public sealed record MonitorEvent(string Time, string Title, string Description, string Tone, string Glyph);
