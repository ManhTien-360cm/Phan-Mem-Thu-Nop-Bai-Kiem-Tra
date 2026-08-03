using ExamTransfer.Infrastructure.Services;

namespace ExamTransfer.LocalServer.Workers;

public sealed class OnlyLanRealtimeOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OnlyLanRealtimeOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<OnlyLanStudentNotificationDispatcher>();
                await dispatcher.DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OnlyLAN realtime outbox worker failed");
            }
        }
    }
}
