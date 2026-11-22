using System.Collections.Concurrent;
using System.Threading.Channels;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Intake;

public enum FollowUpTarget
{
    GitHubRepository,
    Octopus,
    Azure
}

public sealed record FollowUpRefresh(FollowUpTarget Target, string? Repository = null)
{
    public static readonly FollowUpRefresh Octopus = new(FollowUpTarget.Octopus);
    public static readonly FollowUpRefresh Azure = new(FollowUpTarget.Azure);
    public static FollowUpRefresh GitHubRepository(string repository) => new(FollowUpTarget.GitHubRepository, repository);
}

/// <summary>
/// Coalesces webhook-triggered reconciliation reads so a burst of webhooks results in one bounded
/// refresh per target (keeps GitHub/Octopus API usage inside budget).
/// </summary>
public sealed partial class FollowUpRefreshScheduler(
    IGitHubSyncService gitHub,
    IOctopusSyncService octopus,
    IAzureSyncService azure,
    DriftReconciliationService drift,
    IOptions<PollingOptions> options,
    TimeProvider timeProvider,
    ILogger<FollowUpRefreshScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);
    private readonly Channel<FollowUpRefresh> _channel = Channel.CreateUnbounded<FollowUpRefresh>();
    private readonly ConcurrentDictionary<FollowUpRefresh, byte> _pending = new();
    private readonly ConcurrentDictionary<FollowUpRefresh, DateTimeOffset> _lastRun = new();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Follow-up refresh {Target} {Repository} failed")]
    private partial void LogFollowUpFailed(Exception ex, FollowUpTarget target, string? repository);

    public void Request(FollowUpRefresh refresh)
    {
        if (!options.Value.EnableBackgroundWorkers)
        {
            return;
        }
        if (_pending.TryAdd(refresh, 0))
        {
            _channel.Writer.TryWrite(refresh);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var refresh in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            if (_lastRun.TryGetValue(refresh, out var last))
            {
                var wait = last + MinInterval - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }
            }

            _pending.TryRemove(refresh, out _);
            _lastRun[refresh] = timeProvider.GetUtcNow();
            try
            {
                await RunAsync(refresh, stoppingToken);
                await drift.EvaluateAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFollowUpFailed(ex, refresh.Target, refresh.Repository);
            }
        }
    }

    private Task RunAsync(FollowUpRefresh refresh, CancellationToken ct)
        => refresh.Target switch
        {
            FollowUpTarget.GitHubRepository => gitHub.SyncRepositoryAsync(refresh.Repository!, ct),
            FollowUpTarget.Octopus => octopus.SyncAsync(fullBackfill: false, ct),
            FollowUpTarget.Azure => azure.SyncAsync(fullBackfill: false, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(refresh), refresh.Target, null)
        };
}
