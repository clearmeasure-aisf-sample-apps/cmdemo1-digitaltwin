using DigitalTwin.Application.Intake;
using DigitalTwin.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Workers;

/// <summary>Runs raw intake retention once a day (first run shortly after start).</summary>
public sealed partial class RawIntakeRetentionHostedService(
    RawIntakeRetentionService retention,
    IOptions<RetentionOptions> retentionOptions,
    IOptions<PollingOptions> pollingOptions,
    TimeProvider timeProvider,
    ILogger<RawIntakeRetentionHostedService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Raw intake retention: archived {Days} day(s), {Events} event(s); skipped: {Skipped}")]
    private partial void LogRun(int days, int events, string? skipped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raw intake retention failed; next attempt at the next interval")]
    private partial void LogFailed(Exception ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!pollingOptions.Value.EnableBackgroundWorkers || !retentionOptions.Value.Enabled)
        {
            return;
        }
        await Task.Delay(TimeSpan.FromMinutes(10), timeProvider, stoppingToken);
        using var timer = new PeriodicTimer(retentionOptions.Value.Interval, timeProvider);
        do
        {
            try
            {
                var report = await retention.StartOrJoin(null).WaitAsync(stoppingToken);
                var archived = report.Days.Sum(d => d.Archived);
                LogRun(report.Days.Count, archived, report.Skipped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
