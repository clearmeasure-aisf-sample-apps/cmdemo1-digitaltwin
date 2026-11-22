using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Services;

public sealed class AlertRuleOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan EvaluateEvery { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>A change with a release candidate and no activity for this long (not delivered) is stalled.</summary>
    public double StallHours { get; set; } = 6;
    public double FailureLookbackHours { get; set; } = 24;
}

/// <summary>The alert conditions, as a pure function of chains, intake health and the latest reconciliation.</summary>
public static class AlertRules
{
    private static readonly ChainStageKind[] DeliveryStages = [ChainStageKind.Tdd, ChainStageKind.Uat, ChainStageKind.Prod];
    private static readonly ChainStageKind[] PastBuild = [ChainStageKind.ReleaseCandidate, ChainStageKind.OctopusRelease, ChainStageKind.Tdd, ChainStageKind.Uat, ChainStageKind.Prod];

    /// <summary>A newer change of the same deployable that reached the same stage or further: this one was skipped, not stuck.</summary>
    private static bool Superseded(ChangeChain chain, ChainStageKind current, IReadOnlyList<ChangeChain> chains)
        => chains.Any(other => other.ChainId != chain.ChainId
            && string.Equals(other.Deployable, chain.Deployable, StringComparison.OrdinalIgnoreCase)
            && other.LastActivityAt > chain.LastActivityAt
            && other.CurrentStage is { } stage && stage >= current);

    public static IReadOnlyList<TwinAlert> Evaluate(
        IReadOnlyList<ChangeChain> chains, IntakeQueueStatus intake, ReconciliationReport? reconciliation, DateTimeOffset now, AlertRuleOptions options)
    {
        var alerts = new List<TwinAlert>();
        foreach (var chain in chains)
        {
            foreach (var stage in chain.Stages.Where(s => DeliveryStages.Contains(s.Kind) && s.Status == ChainStageStatus.Failed))
            {
                var at = stage.FinishedAt ?? stage.StartedAt ?? chain.LastActivityAt;
                if (now - at <= TimeSpan.FromHours(options.FailureLookbackHours))
                {
                    var target = stage.Kind.ToString().ToLowerInvariant();
                    alerts.Add(new TwinAlert(AlertKind.DeployFailed, stage.Kind == ChainStageKind.Prod ? "critical" : "warning",
                        $"deploy-failed:{stage.Reference ?? chain.ChainId + ":" + target}",
                        $"Deploy to {target} failed: {chain.Deployable ?? chain.PipelineId} {chain.Version}",
                        $"{chain.Title} ({stage.Detail ?? "failed"}, attempt {stage.Attempts}).", stage.Url, at));
                }
            }

            var idle = now - chain.LastActivityAt;
            if (chain.Outcome is ChainOutcome.InProgress or ChainOutcome.Waiting && chain.CurrentStage is { } current && PastBuild.Contains(current)
                && idle >= TimeSpan.FromHours(options.StallHours) && idle <= TimeSpan.FromDays(7) && !Superseded(chain, current, chains))
            {
                alerts.Add(new TwinAlert(AlertKind.StalledChange, "warning", $"stalled:{chain.ChainId}:{current}",
                    $"Change stalled at {current}: {chain.Deployable ?? chain.PipelineId} {chain.Version}",
                    $"{chain.Title}: no progress for {idle.TotalHours:0.#} h ({chain.Outcome}).",
                    chain.Stages.LastOrDefault(s => s.Url is not null)?.Url, chain.LastActivityAt));
            }
        }

        if (intake.DeadLetterCount > 0)
        {
            alerts.Add(new TwinAlert(AlertKind.DeadLetters, "critical", $"dead-letters:{intake.DeadLetterCount}",
                $"{intake.DeadLetterCount} intake event(s) dead-lettered",
                "Captured events failed processing after all retries. Inspect GET /api/admin/deadletters and replay after the fix.", null, now));
        }

        if (reconciliation is { DriftCount: > 0 } report)
        {
            var drifted = report.Checks.Where(c => c.Error is null && !c.InSync).Select(c => $"{c.Source} {c.Name} ({c.Scope})");
            alerts.Add(new TwinAlert(AlertKind.ReconciliationDrift, "warning", $"drift:{report.FinishedAt.UtcTicks}",
                $"Reconciliation found {report.DriftCount} drifted check(s)", string.Join("; ", drifted), null, report.FinishedAt));
        }
        return alerts;
    }
}

public sealed record AlertsDto(bool SinkEnabled, string Format, DateTimeOffset? LastEvaluatedAt, IReadOnlyList<TwinAlert> Active, IReadOnlyList<SentAlert> Recent);

public sealed record SentAlert(TwinAlert Alert, bool Delivered, string? Error);

/// <summary>
/// Evaluates the rules and sends each new condition once. The first evaluation after a start only records what is
/// already true, so restarts and deploys do not re-send old alerts.
/// </summary>
public sealed class AlertService(
    TwinQueryService queries,
    IIntakeQueueMonitor intake,
    ReconciliationService reconciliation,
    IAlertSink sink,
    IOptions<AlertRuleOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private const int RecentLimit = 50;
    private readonly SemaphoreSlim _evaluating = new(1, 1);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly List<SentAlert> _recent = [];
    private bool _baselined;
    private IReadOnlyList<TwinAlert> _active = [];
    private DateTimeOffset? _lastEvaluatedAt;

    public AlertsDto Snapshot()
    {
        lock (_recent)
        {
            return new AlertsDto(sink.IsEnabled, sink.Format, _lastEvaluatedAt, _active, _recent.ToList());
        }
    }

    public async Task<AlertsDto> EvaluateAsync(CancellationToken ct)
    {
        await _evaluating.WaitAsync(ct);
        try
        {
            var now = timeProvider.GetUtcNow();
            var chains = (await queries.GetChangeChainsAsync(null, 80, null, ct)).Chains;
            var current = AlertRules.Evaluate(chains, await intake.GetStatusAsync(ct), reconciliation.LastReport, now, options.Value);
            _active = current;
            _lastEvaluatedAt = now;
            if (!_baselined)
            {
                _known.UnionWith(current.Select(a => a.Key));
                _baselined = true;
                return Snapshot();
            }
            foreach (var alert in current.Where(a => _known.Add(a.Key)))
            {
                await DeliverAsync(alert, ct);
            }
            return Snapshot();
        }
        finally
        {
            _evaluating.Release();
        }
    }

    public async Task<SentAlert> SendTestAsync(CancellationToken ct)
    {
        var alert = new TwinAlert(AlertKind.Test, "info", $"test:{timeProvider.GetUtcNow().UtcTicks}", "cmdemo1 digital twin: test alert",
            "The alert webhook is configured correctly.", null, timeProvider.GetUtcNow());
        return await DeliverAsync(alert, ct);
    }

    private async Task<SentAlert> DeliverAsync(TwinAlert alert, CancellationToken ct)
    {
        SentAlert sent;
        try
        {
            await sink.SendAsync(alert, ct);
            sent = new SentAlert(alert, sink.IsEnabled, sink.IsEnabled ? null : "alert webhook not configured");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sent = new SentAlert(alert, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        lock (_recent)
        {
            _recent.Insert(0, sent);
            if (_recent.Count > RecentLimit)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }
        return sent;
    }

    public void Dispose() => _evaluating.Dispose();
}
