using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Application.Intake;

public sealed record RebuildKindComparison(int Live, int FromRaw, int MissingFromRaw, int OnlyInRaw, IReadOnlyList<string> MissingSample);

/// <summary>What a rebuild would produce, compared with the live read models, without touching either.</summary>
public sealed record RebuildPreflightReport(bool CanRebuild, int StateObservationsFolded, IReadOnlyDictionary<string, RebuildKindComparison> Kinds);

/// <summary>
/// Folds every processed raw state observation (archive and SQL) in memory (the same fold as time travel) and compares entity keys with the
/// live read models. A live row the raw store cannot reproduce would be lost by a rebuild, so the rebuild refuses to start.
/// </summary>
public sealed class RebuildPreflight(RawIntakeHistory history, ITwinStore store)
{
    private const int PageSize = 200;

    public async Task<RebuildPreflightReport> RunAsync(CancellationToken ct)
    {
        var latest = new Dictionary<(string Kind, string Subject), ObservedEvent>();
        var drift = new List<ObservedEvent>();
        var folded = 0;
        await foreach (var page in history.ReadPagesAsync(PageSize, ct))
        {
            foreach (var raw in page.Where(r => r.ProcessedAt is not null && r.Kind == IntakeKind.ObservedBatch))
            {
                foreach (var ev in IntakeNormalizer.Normalize(raw).Where(StateObservations.IsStateEvent))
                {
                    folded++;
                    if (ev.SubjectType == StateKinds.Drift.Name)
                    {
                        drift.Add(ev);
                        continue;
                    }
                    var key = (ev.SubjectType, ev.SubjectId.ToUpperInvariant());
                    if (!latest.TryGetValue(key, out var current) || StateObservations.IsNewer(ev.OccurredAt, ev.IdempotencyKey, current.OccurredAt, current.IdempotencyKey))
                    {
                        latest[key] = ev;
                    }
                }
            }
        }

        var fromRaw = await CollectKeysAsync(PointInTimeReadModel.Build(DateTimeOffset.MaxValue, latest.Values, drift), ct);
        var live = await CollectKeysAsync(store, ct);
        var kinds = new SortedDictionary<string, RebuildKindComparison>(StringComparer.Ordinal);
        foreach (var kind in StateKinds.All)
        {
            var liveKeys = live[kind.Name];
            var rawKeys = fromRaw[kind.Name];
            var missing = liveKeys.Where(k => !rawKeys.Contains(k)).Order(StringComparer.Ordinal).ToList();
            kinds[kind.Name] = new RebuildKindComparison(liveKeys.Count, rawKeys.Count, missing.Count, rawKeys.Count(k => !liveKeys.Contains(k)), missing.Take(5).ToList());
        }
        return new RebuildPreflightReport(kinds.Values.All(k => k.MissingFromRaw == 0), folded, kinds);
    }

    private static async Task<Dictionary<string, HashSet<string>>> CollectKeysAsync(ITwinReadModel model, CancellationToken ct)
    {
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add<T>(StateKind kind, IEnumerable<T> rows) where T : class
            => keys[kind.Name] = rows.Select(r => kind.Key(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Add(StateKinds.Repository, await model.GetRepositoriesAsync(ct));
        Add(StateKinds.SourceChange, await model.GetSourceChangesAsync(ct));
        Add(StateKinds.PipelineRun, await model.GetPipelineRunsAsync(int.MaxValue, ct));
        Add(StateKinds.Artifact, await model.GetArtifactsAsync(ct));
        Add(StateKinds.WorkItem, await model.GetWorkItemsAsync(ct));
        Add(StateKinds.Release, await model.GetReleasesAsync(int.MaxValue, ct));
        Add(StateKinds.Delivery, await model.GetDeliveriesAsync(int.MaxValue, ct));
        Add(StateKinds.Runbook, await model.GetRunbooksAsync(int.MaxValue, ct));
        Add(StateKinds.Freeze, await model.GetFreezesAsync(ct));
        var desired = await model.GetDesiredConfigAsync(ct);
        Add(StateKinds.DesiredConfig, desired is null ? [] : new[] { desired });
        Add(StateKinds.Environment, await model.GetEnvironmentInstancesAsync(ct));
        Add(StateKinds.Workload, await model.GetWorkloadsAsync(ct));
        Add(StateKinds.AzureResource, await model.GetAzureResourcesAsync(ct));
        Add(StateKinds.HealthProbe, await model.GetHealthProbesAsync(ct));
        Add(StateKinds.Drift, await model.GetDriftFindingsAsync(includeResolved: true, ct));
        Add(StateKinds.WebhookStatus, await model.GetWebhookStatusesAsync(ct));
        return keys;
    }
}
