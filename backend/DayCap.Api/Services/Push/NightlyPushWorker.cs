namespace DayCap.Api.Services.Push;

/// <summary>每分鐘看一次誰到了每晚通知的時間（§9.4）。</summary>
public class NightlyPushWorker(IServiceScopeFactory scopes, ILogger<NightlyPushWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPushService>().SendDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Nightly push round failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
