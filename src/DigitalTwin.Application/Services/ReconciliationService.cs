using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Services;

public sealed class ReconciliationOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

public sealed record ReconciliationReport(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int DriftCount, int ErrorCount, IReadOnlyList<ReconciliationCheck> Checks);

/// <summary>Compares the twin with GitHub and Octopus (daily, or on demand) and keeps the latest report.</summary>
public sealed class ReconciliationService(IEnumerable<ISourceReconciler> reconcilers, ITwinStore store, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public ReconciliationReport? LastReport { get; private set; }

    public void Dispose() => _running.Dispose();

    public async Task<ReconciliationReport> RunAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            var startedAt = timeProvider.GetUtcNow();
            var checks = new List<ReconciliationCheck>();
            foreach (var reconciler in reconcilers)
            {
                try
                {
                    checks.AddRange(await reconciler.CheckAsync(store, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    checks.Add(new ReconciliationCheck(reconciler.Source, "all", "-", 0, 0, [], [], $"{ex.GetType().Name}: {ex.GetBaseException().Message}"));
                }
            }
            return LastReport = new ReconciliationReport(startedAt, timeProvider.GetUtcNow(),
                checks.Count(c => c.Error is null && !c.InSync), checks.Count(c => c.Error is not null), checks);
        }
        finally
        {
            _running.Release();
        }
    }
}
