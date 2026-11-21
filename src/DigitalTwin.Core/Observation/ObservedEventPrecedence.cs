namespace DigitalTwin.Core.Observation;

/// <summary>
/// When several observations share an idempotency key (redeliveries, webhook/poll overlap), the journal keeps
/// the earliest one. The rule is independent of processing order, so live processing and a rebuild from the
/// raw intake table converge on the same journal.
/// </summary>
public static class ObservedEventPrecedence
{
    public static bool Precedes(ObservedEvent candidate, ObservedEvent existing)
    {
        var byTime = candidate.IngestedAt.UtcTicks.CompareTo(existing.IngestedAt.UtcTicks);
        if (byTime != 0)
        {
            return byTime < 0;
        }

        var byPayload = string.CompareOrdinal(candidate.PayloadJson, existing.PayloadJson);
        if (byPayload != 0)
        {
            return byPayload < 0;
        }

        return string.CompareOrdinal(candidate.Summary, existing.Summary) < 0;
    }

    public static ObservedEvent Earliest(IEnumerable<ObservedEvent> observations)
        => observations.Aggregate((best, next) => Precedes(next, best) ? next : best);
}
