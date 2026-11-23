using DigitalTwin.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Workers;

/// <summary>Keeps the storage readout warm: first computation a few minutes after start (past the post-deploy rush), then every 5 minutes.</summary>
public sealed partial class StorageReadoutHostedService(
    StorageReadout readout,
    IOptions<PollingOptions> pollingOptions,
    TimeProvider timeProvider,
    ILogger<StorageReadoutHostedService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage readout failed; next attempt at the next interval")]
    private partial void LogFailed(Exception ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!pollingOptions.Value.EnableBackgroundWorkers)
        {
            return;
        }
        await Task.Delay(TimeSpan.FromMinutes(3), timeProvider, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), timeProvider);
        do
        {
            try
            {
                await readout.Refresh().WaitAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
