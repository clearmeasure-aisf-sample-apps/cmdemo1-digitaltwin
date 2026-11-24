using Microsoft.Data.SqlClient;

namespace DigitalTwin.Messaging;

/// <summary>Failures that say "the database is busy", not "this event is bad": worth waiting out before dead-lettering.</summary>
public static class TransientDatabaseFailure
{
    // Timeout, deadlock victim, lock timeout, and Azure SQL throttling / failover / connection drops.
    private static readonly HashSet<int> SqlNumbers = [-2, 1205, 1222, 40501, 40613, 40197, 49918, 49919, 49920, 10928, 10929, 4221, 233, 64, 10053, 10054, 10060];

    public static bool Matches(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException || (e is SqlException sql && SqlNumbers.Contains(sql.Number)))
            {
                return true;
            }
        }
        return false;
    }
}
