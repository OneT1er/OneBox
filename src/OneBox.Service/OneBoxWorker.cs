using Microsoft.Extensions.Hosting;

namespace OneBox.Service;

internal sealed class OneBoxWorker(SessionManager sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceLog.Write("service host started");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await sessions.RefreshAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await sessions.StopAsync().ConfigureAwait(false);
            ServiceLog.Write("service host stopped");
        }
    }
}
