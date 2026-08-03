using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IAsyncInitializable, IDisposable
{
    private readonly IBackendClient api;
    private readonly IServerClock serverClock;
    private readonly ICountdownTicker ticker;
    private readonly CancellationTokenSource disposeCts = new();
    private string status = "Chưa có dữ liệu tổng quan";
    private bool isBusy;
    private bool initialized;
    private bool hasSuccessfulLoad;
    private bool disposed;

    public DashboardViewModel(IBackendClient api)
        : this(api, AppServices.ServerClock, AppServices.CountdownTickers.Create(TimeSpan.FromSeconds(1)))
    {
    }

    public DashboardViewModel(IBackendClient api, IServerClock serverClock, ICountdownTicker ticker)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock));
        this.ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        ShowEmptyMetrics();
        ticker.Tick += OnTick;
    }

    public ObservableCollection<MetricCard> Metrics { get; } = new();

    public ObservableCollection<ActivityItem> Activities { get; } = new();

    public ObservableCollection<AlertItem> Alerts { get; } = new();

    public ActiveSessionCard? ActiveSession { get; private set; }

    public bool HasActiveSession => ActiveSession is not null;

    public bool HasActivities => Activities.Count > 0;

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value) && RefreshCommand is AsyncRelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized || disposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);
        try
        {
            await LoadAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            initialized = true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ticker.Tick -= OnTick;
        ticker.Dispose();
        disposeCts.Cancel();
        disposeCts.Dispose();
    }

    private Task LoadAsync() => LoadAsync(disposeCts.Token);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return;
        }

        await RunOnUiAsync(() =>
        {
            IsBusy = true;
            Status = "Đang tải dữ liệu tổng quan";
        });
        try
        {
            var response = await api.GetDashboardAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (response?.Success == true && response.Data is not null)
            {
                await RunOnUiAsync(() => ApplyDashboard(response.Data));
            }
            else
            {
                var message = response?.Error?.Message ?? "Máy chủ không trả về dữ liệu tổng quan hợp lệ.";
                await RunOnUiAsync(() => ApplyLoadFailure(message));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var traceId = FrontendLogger.Log(ex, "DashboardViewModel.LoadAsync");
            await RunOnUiAsync(() => ApplyLoadFailure($"{ex.Message} Mã tra cứu: {traceId}."));
        }
        finally
        {
            if (!disposed)
            {
                await RunOnUiAsync(() => IsBusy = false);
            }
        }
    }

    private void ApplyDashboard(ExamTransfer.Shared.Contracts.DashboardSummaryDto data)
    {
        Metrics.Clear();
        Metrics.Add(new("Lớp học", data.ClassCount.ToString("N0"), "đang hoạt động", "\uE716", "primary", "Dữ liệu từ máy chủ"));
        Metrics.Add(new("Bài kiểm tra", data.ExamCount.ToString("N0"), "chưa lưu trữ", "\uE8A5", "accent", "Dữ liệu từ máy chủ"));
        Metrics.Add(new("Phòng đang chạy", data.ActiveSessionCount.ToString("N0"), "phiên hoạt động", "\uE9D2", "success", "Dữ liệu từ máy chủ"));
        Metrics.Add(new("Chưa chấm", data.PendingGradingCount.ToString("N0"), "bài cần xử lý", "\uE70B", "warning", "Dữ liệu từ máy chủ"));

        Alerts.Clear();
        foreach (var warning in data.Warnings)
        {
            Alerts.Add(new("Cảnh báo hệ thống", warning, "warning", "\uE7BA"));
        }

        Activities.Clear();
        foreach (var recentSession in data.RecentSessions)
        {
            var activityTime = recentSession.EndTimeUtc
                ?? recentSession.StartTimeUtc
                ?? recentSession.ServerNowUtc;
            Activities.Add(new(
                activityTime.ToLocalTime().ToString("HH:mm"),
                recentSession.Title,
                $"Phòng {recentSession.RoomCode}",
                "primary",
                "\uE9D2"));
        }
        Raise(nameof(HasActivities));

        if (data.ActiveSession is { } synchronizedSession)
        {
            serverClock.Synchronize(synchronizedSession.ServerNowUtc);
        }

        ActiveSession = data.ActiveSession is { } session
            ? new ActiveSessionCard(
                session.Title,
                session.RoomCode,
                session.Status,
                session.Counts.Total,
                session.Counts.Connected,
                session.Counts.Submitted,
                session.EffectiveDeadlineUtc,
                serverClock)
            : null;
        Raise(nameof(ActiveSession));
        Raise(nameof(HasActiveSession));
        UpdateTickerState();

        hasSuccessfulLoad = true;
        Status = data.Warnings.Count == 0
            ? "Đã đồng bộ dữ liệu thật từ máy chủ"
            : $"Đã đồng bộ; có {data.Warnings.Count} cảnh báo cần xem";
    }

    private void ApplyLoadFailure(string message)
    {
        foreach (var existing in Alerts.Where(x => x.Title == "Không thể làm mới dữ liệu").ToList())
        {
            Alerts.Remove(existing);
        }

        if (!hasSuccessfulLoad)
        {
            ShowEmptyMetrics();
            ActiveSession = null;
            Activities.Clear();
            Alerts.Clear();
            Raise(nameof(ActiveSession));
            Raise(nameof(HasActiveSession));
            Raise(nameof(HasActivities));
        }

        UpdateTickerState();

        Alerts.Add(new("Không thể làm mới dữ liệu", message, "danger", "\uE783"));
        Status = hasSuccessfulLoad
            ? "Mất kết nối; đang giữ dữ liệu thật tải thành công gần nhất"
            : "Không có dữ liệu tổng quan vì máy chủ chưa phản hồi";
    }

    private void ShowEmptyMetrics()
    {
        Metrics.Clear();
        Metrics.Add(new("Lớp học", "--", "chưa có dữ liệu", "\uE716", "primary", "Chờ máy chủ phản hồi"));
        Metrics.Add(new("Bài kiểm tra", "--", "chưa có dữ liệu", "\uE8A5", "accent", "Chờ máy chủ phản hồi"));
        Metrics.Add(new("Phòng đang chạy", "--", "chưa có dữ liệu", "\uE9D2", "success", "Chờ máy chủ phản hồi"));
        Metrics.Add(new("Chưa chấm", "--", "chưa có dữ liệu", "\uE70B", "warning", "Chờ máy chủ phản hồi"));
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (disposed || ActiveSession?.IsCountdownVisible != true)
        {
            return;
        }

        ActiveSession.RefreshTime();
    }

    private void UpdateTickerState()
    {
        if (disposed)
        {
            return;
        }

        if (ActiveSession?.IsCountdownVisible == true)
        {
            if (!ticker.IsRunning)
            {
                ticker.Start();
            }
        }
        else if (ticker.IsRunning)
        {
            ticker.Stop();
        }
    }
}

public sealed record MetricCard(string Title, string Value, string Subtitle, string Glyph, string Tone, string Trend);
public sealed record ActivityItem(string Time, string Title, string Description, string Tone, string Glyph);
public sealed record AlertItem(string Title, string Description, string Tone, string Glyph);
public sealed class ActiveSessionCard : ObservableObject
{
    private readonly IServerClock serverClock;
    private DateTimeOffset? effectiveDeadlineUtc;
    private string timeLeft;

    public ActiveSessionCard(
        string title,
        string roomCode,
        SessionStatus status,
        int total,
        int connected,
        int submitted,
        DateTimeOffset? effectiveDeadlineUtc,
        IServerClock serverClock)
    {
        Title = title;
        RoomCode = roomCode;
        Status = status;
        Total = total;
        Connected = connected;
        Submitted = submitted;
        this.serverClock = serverClock;
        this.effectiveDeadlineUtc = SupportsCountdown(status)
            ? effectiveDeadlineUtc
            : null;
        timeLeft = IsTerminal
            ? StatusDisplayText
            : ServerCountdown.Format(ServerCountdown.Remaining(serverClock, this.effectiveDeadlineUtc));
    }

    public string Title { get; }
    public string RoomCode { get; }
    public SessionStatus Status { get; }
    public string StatusDisplayText => Status switch
    {
        SessionStatus.Draft => "Bản nháp",
        SessionStatus.Waiting => "Đang chờ",
        SessionStatus.Distributing => "Đang phát đề",
        SessionStatus.InProgress => "Đang diễn ra",
        SessionStatus.Paused => "Tạm dừng",
        SessionStatus.Collecting => "Đang thu bài",
        SessionStatus.Finished => "Đã kết thúc",
        SessionStatus.Cancelled => "Đã hủy",
        SessionStatus.Archived => "Đã lưu trữ",
        _ => Status.ToString()
    };
    public bool IsTerminal => Status is
        SessionStatus.Finished or
        SessionStatus.Cancelled or
        SessionStatus.Archived;
    public bool IsCountdownVisible =>
        SupportsCountdown(Status) && effectiveDeadlineUtc.HasValue;
    public string TimeLeftLabel => IsTerminal ? StatusDisplayText : "Thời gian còn lại";
    public int Total { get; }
    public int Connected { get; }
    public int Submitted { get; }
    public string TimeLeft { get => timeLeft; private set => Set(ref timeLeft, value); }
    public double ConnectedPercent => Total <= 0 ? 0 : Connected * 100d / Total;
    public double SubmittedPercent => Total <= 0 ? 0 : Submitted * 100d / Total;

    public void UpdateDeadline(DateTimeOffset? deadlineUtc)
    {
        if (!SupportsCountdown(Status))
        {
            return;
        }

        effectiveDeadlineUtc = deadlineUtc;
        Raise(nameof(IsCountdownVisible));
        RefreshTime();
    }

    public void RefreshTime()
    {
        if (IsTerminal)
        {
            return;
        }

        TimeLeft = IsCountdownVisible
            ? ServerCountdown.Format(ServerCountdown.Remaining(serverClock, effectiveDeadlineUtc))
            : "--:--:--";
    }

    private static bool SupportsCountdown(SessionStatus status) => status is
        SessionStatus.Waiting or
        SessionStatus.InProgress or
        SessionStatus.Paused or
        SessionStatus.Collecting;
}
