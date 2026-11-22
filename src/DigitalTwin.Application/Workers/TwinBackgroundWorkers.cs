using DigitalTwin.Core.Ports;
using DigitalTwin.Application.Intake;
using DigitalTwin.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Workers;

public interface IBackfillCoordinator
{
    Task<BackfillReport> RebuildAllAsync(string reason, CancellationToken ct);
}

public sealed record BackfillReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool GitHubOk,
    bool OctopusOk,
    bool AzureOk,
    bool ProbesOk,
    int ActiveDriftCount,
    IReadOnlyList<string> Notes);

public sealed partial class BackfillCoordinator(
    IGitHubSyncService gitHub,
    IOctopusSyncService octopus,
    IAzureSyncService azure,
    IHealthProbeService probes,
    DriftReconciliationService drift,
    IRealtimeEventPublisher realtime,
    TimeProvider timeProvider,
    ILogger<BackfillCoordinator> logger) : IBackfillCoordinator, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backfill step {Step} failed")]
    private partial void LogBackfillStepFailed(Exception ex, string step);

    public async Task<BackfillReport> RebuildAllAsync(string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var startedAt = timeProvider.GetUtcNow();
            var notes = new List<string> { $"Trigger: {reason}" };
            var ghOk = false;
            var octoOk = false;
            var azOk = false;
            var probeOk = false;

            var ghTask = Task.Run(async () =>
            {
                try
                {
                    await gitHub.SyncAsync(fullBackfill: true, ct);
                    ghOk = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    lock (notes) { notes.Add($"GitHub: {ex.Message}"); }
                    LogBackfillStepFailed(ex, "GitHub");
                }
            }, ct);

            var octoTask = Task.Run(async () =>
            {
                try
                {
                    await octopus.SyncAsync(fullBackfill: true, ct);
                    octoOk = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    lock (notes) { notes.Add($"Octopus: {ex.Message}"); }
                    LogBackfillStepFailed(ex, "Octopus");
                }
            }, ct);

            var azTask = Task.Run(async () =>
            {
                try
                {
                    await azure.SyncAsync(fullBackfill: true, ct);
                    azOk = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    lock (notes) { notes.Add($"Azure: {ex.Message}"); }
                    LogBackfillStepFailed(ex, "Azure");
                }
            }, ct);

            await Task.WhenAll(ghTask, octoTask, azTask);

            try
            {
                await probes.ProbeAllAsync(ct);
                probeOk = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                notes.Add($"Probes: {ex.Message}");
                LogBackfillStepFailed(ex, "Probes");
            }

            var drifts = await drift.EvaluateAsync(ct);
            var finishedAt = timeProvider.GetUtcNow();
            await realtime.PublishStateRefreshedAsync("backfill-completed", finishedAt, ct);

            return new BackfillReport(startedAt, finishedAt, ghOk, octoOk, azOk, probeOk, drifts.Count, notes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

public sealed partial class TwinPollingHostedService(
    IBackfillCoordinator backfill,
    IGitHubSyncService gitHub,
    IGitHubRateLimitProvider ghClient,
    IOctopusSyncService octopus,
    IAzureSyncService azure,
    IHealthProbeService probes,
    DriftReconciliationService drift,
    ProjectionBaselineSeeder baseline,
    IOptions<PollingOptions> options,
    ILogger<TwinPollingHostedService> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded existing read models into the raw store as baseline state observations")]
    private partial void LogBaselineSeeded();
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting initial digital twin backfill...")]
    private partial void LogBackfillStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "Initial backfill completed: GitHub={Gh}, Octopus={Octo}, Azure={Az}, Probes={Pr}, ActiveDrifts={Dr}")]
    private partial void LogBackfillCompleted(bool gh, bool octo, bool az, bool pr, int dr);

    [LoggerMessage(Level = LogLevel.Error, Message = "Initial backfill failed; reconciliation timers will retry")]
    private partial void LogBackfillError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Periodic {Worker} reconciliation tick failed")]
    private partial void LogReconciliationFailed(Exception ex, string worker);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableBackgroundWorkers)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        try
        {
            if (await baseline.SeedIfNeededAsync(stoppingToken))
            {
                LogBaselineSeeded();
            }
            LogBackfillStarting();
            var report = await backfill.RebuildAllAsync("startup-backfill", stoppingToken);
            LogBackfillCompleted(report.GitHubOk, report.OctopusOk, report.AzureOk, report.ProbesOk, report.ActiveDriftCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            LogBackfillError(ex);
        }

        using var ghTimer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(180, options.Value.GitHubPollSeconds)));
        using var ghBoardTimer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(180, options.Value.GitHubBoardPollSeconds)));
        using var octoTimer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(20, options.Value.OctopusPollSeconds)));
        using var azTimer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(30, options.Value.AzurePollSeconds)));
        using var probeTimer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(20, options.Value.ProbeIntervalSeconds)));

        var ghBackoffCounter = 0;
        var needsGitHubBackfill = ghClient.IsRestPaused(out _);

        await Task.WhenAll(
            RunLoopAsync(ghTimer, "GitHub", async ct =>
            {
                // Stretch reconciliation 3x when in backoff mode (<1,000 remaining)
                if (!needsGitHubBackfill && ghClient.IsRestBackoff() && (++ghBackoffCounter % 3) != 0)
                {
                    return;
                }
                if (needsGitHubBackfill && ghClient.IsRestPaused(out _))
                {
                    return;
                }

                var runFullBackfill = needsGitHubBackfill;
                await gitHub.SyncAsync(fullBackfill: runFullBackfill, ct);
                if (runFullBackfill && !ghClient.IsRestPaused(out _))
                {
                    needsGitHubBackfill = false;
                }
                await drift.EvaluateAsync(ct);
            }, stoppingToken),
            RunLoopAsync(ghBoardTimer, "GitHubBoard", async ct =>
            {
                await gitHub.SyncBoardOnlyAsync(ct);
            }, stoppingToken),
            RunLoopAsync(octoTimer, "Octopus", async ct =>
            {
                await octopus.SyncAsync(fullBackfill: false, ct);
                await drift.EvaluateAsync(ct);
            }, stoppingToken),
            RunLoopAsync(azTimer, "Azure", async ct =>
            {
                await azure.SyncAsync(fullBackfill: false, ct);
                await drift.EvaluateAsync(ct);
            }, stoppingToken),
            RunLoopAsync(probeTimer, "HealthProbes", async ct =>
            {
                await probes.ProbeAllAsync(ct);
                await drift.EvaluateAsync(ct);
            }, stoppingToken));
    }

    private async Task RunLoopAsync(PeriodicTimer timer, string name, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await action(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogReconciliationFailed(ex, name);
            }
        }
    }
}
