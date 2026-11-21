namespace DigitalTwin.Domain.Observation;

public sealed record GitHubRateLimitStatus(
    int? CoreLimit,
    int? CoreRemaining,
    int? CoreUsed,
    DateTimeOffset? CoreResetAt,
    int? GraphQlLimit,
    int? GraphQlRemaining,
    DateTimeOffset? GraphQlResetAt,
    string Mode, // normal | backoff | paused | unknown
    long TwinRestCallsThisHour,
    long TwinRestCallsTotal,
    long TwinCache304HitsTotal,
    long TwinGraphQlCallsTotal,
    long TwinCallsSkippedByQuota,
    int BudgetPerHourTarget);
