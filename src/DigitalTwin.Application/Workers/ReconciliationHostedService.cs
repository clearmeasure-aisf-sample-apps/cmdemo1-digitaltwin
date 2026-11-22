using DigitalTwin.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Workers;

/// <summary>Daily reconciliation of the twin against GitHub and Octopus (first run 20 minutes after start).</summary>
public sealed partial class ReconciliationHostedService(
    ReconciliationService reconciliation,
    IOptions<ReconciliationOptions> reconciliationOptions,
    IOptions<PollingOptions> pollingOptions,
    TimeProvider timeProvider,
    ILogger<ReconciliationHostedService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation: {Drift} drifted check(s), {Errors} error(s)")]
    private partial void LogRun(int drift, int errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation failed; next attempt at the next interval")]
    private partial void LogFailed(Exception ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!pollingOptions.Value.EnableBackgroundWorkers || !reconciliationOptions.Value.Enabled)
        {
            return;
        }
        await Task.Delay(TimeSpan.FromMinutes(20), timeProvider, stoppingToken);
        using var timer = new PeriodicTimer(reconciliationOptions.Value.Interval, timeProvider);
        do
        {
            try
            {
                var report = await reconciliation.RunAsync(stoppingToken);
                LogRun(report.DriftCount, report.ErrorCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
