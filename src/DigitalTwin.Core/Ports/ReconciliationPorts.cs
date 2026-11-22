namespace DigitalTwin.Core.Ports;

/// <summary>One comparison of the twin against a source of truth (e.g. open PRs of a repository).</summary>
public sealed record ReconciliationCheck(
    string Source,
    string Name,
    string Scope,
    int TwinCount,
    int SourceCount,
    IReadOnlyList<string> MissingInTwin,
    IReadOnlyList<string> ExtraInTwin,
    string? Error = null)
{
    public bool InSync => Error is null && MissingInTwin.Count == 0 && ExtraInTwin.Count == 0;
}

/// <summary>Compares the twin's read models with one source system and asks it to refresh what drifted.</summary>
public interface ISourceReconciler
{
    string Source { get; }
    Task<IReadOnlyList<ReconciliationCheck>> CheckAsync(ITwinReadModel twin, CancellationToken ct);
}
