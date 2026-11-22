using DigitalTwin.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Workers;

/// <summary>Evaluates alert rules every few minutes (first run two minutes after start, as a baseline).</summary>
public sealed partial class AlertHostedService(
    AlertService alerts,
    IOptions<AlertRuleOptions> alertOptions,
    IOptions<PollingOptions> pollingOptions,
    TimeProvider timeProvider,
    ILogger<AlertHostedService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert evaluation failed; next attempt at the next interval")]
    private partial void LogFailed(Exception ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!pollingOptions.Value.EnableBackgroundWorkers || !alertOptions.Value.Enabled)
        {
            return;
        }
        await Task.Delay(TimeSpan.FromMinutes(2), timeProvider, stoppingToken);
        using var timer = new PeriodicTimer(alertOptions.Value.EvaluateEvery, timeProvider);
        do
        {
            try
            {
                await alerts.EvaluateAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
