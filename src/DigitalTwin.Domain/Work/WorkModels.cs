namespace DigitalTwin.Domain.Work;

public sealed class WorkItemSnapshot
{
    public string Id { get; set; } = string.Empty; // "{repo}#{number}"
    public string Repository { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = "open"; // open | closed
    public string Column { get; set; } = "Todo"; // Todo | In Progress | Review | Merged | TDD | UAT | Done
    public string ColumnSource { get; set; } = "project"; // project | derived
    public string WorkerKind { get; set; } = "team"; // bot | team
    public string? Agent { get; set; }
    public string? Assignee { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public string? ReleaseCandidateVersion { get; set; }
    public string? TddVersion { get; set; }
    public DateTimeOffset? TddDeployedAt { get; set; }
    public string? UatVersion { get; set; }
    public DateTimeOffset? UatDeployedAt { get; set; }
    public string? ProdVersion { get; set; }
    public DateTimeOffset? ProdDeployedAt { get; set; }
    public int FtFailures { get; set; }
    public int Reverts { get; set; }
    public double? CycleTimeHours { get; set; }
    public double? LeadTimeHours { get; set; }
    public string ColumnHistoryJson { get; set; } = "[]";
    public string LinkedPrsJson { get; set; } = "[]";
    public string CursorSessionsJson { get; set; } = "[]";
    public string? LatestStatusUrl { get; set; }
    public string? LatestStatusKind { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record WorkBoardSummary(
    int TotalItems,
    int OpenItems,
    int BotItems,
    int TeamItems,
    IReadOnlyDictionary<string, int> WipByColumn,
    double? MedianCycleTimeHours,
    double? P85CycleTimeHours,
    double? MedianLeadTimeHours,
    double? ReworkRate,
    IReadOnlyDictionary<string, int> ThroughputByAgent);
