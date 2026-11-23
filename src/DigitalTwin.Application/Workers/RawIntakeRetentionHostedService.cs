using DigitalTwin.Application.Intake;
using DigitalTwin.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Workers;

/// <summary>Runs raw intake retention, then journal retention, once a day (first run shortly after start).</summary>
public sealed partial class RawIntakeRetentionHostedService(
    RawIntakeRetentionService retention,
    JournalRetentionService journalRetention,
    IOptions<JournalRetentionOptions> journalOptions,
    IOptions<RetentionOptions> retentionOptions,
    IOptions<PollingOptions> pollingOptions,
    TimeProvider timeProvider,
    ILogger<RawIntakeRetentionHostedService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Raw intake retention: archived {Days} day(s), {Events} event(s); skipped: {Skipped}")]
    private partial void LogRun(int days, int events, string? skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Journal retention: archived {Days} day(s), removed {State} superseded state and {Lifecycle} lifecycle row(s); skipped: {Skipped}")]
    private partial void LogJournal(int days, int state, int lifecycle, string? skipped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Journal retention failed; next attempt at the next interval")]
    private partial void LogJournalFailed(Exception ex);

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

            // Independent of raw retention: one failing must not keep the other from running for another day.
            try
            {
                if (journalOptions.Value.Enabled)
                {
                    var journal = await journalRetention.StartOrJoin(null).WaitAsync(stoppingToken);
                    var superseded = journal.Days.Sum(d => d.SupersededStateRemoved);
                    var lifecycle = journal.Days.Sum(d => d.LifecycleRemoved);
                    LogJournal(journal.Days.Count, superseded, lifecycle, journal.Skipped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogJournalFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
