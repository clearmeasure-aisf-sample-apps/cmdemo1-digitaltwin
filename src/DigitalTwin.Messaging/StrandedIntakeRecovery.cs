using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine.Runtime;

namespace DigitalTwin.Messaging;

/// <summary>
/// Re-sends captured raw events that are still unprocessed (and not dead-lettered) well after capture. Messages left in a
/// transport the twin no longer listens to (after switching between the SQL Server and Service Bus queues), or lost by a
/// broker, are picked up again; the handler skips rows that were processed meanwhile, so duplicates are harmless.
/// </summary>
public sealed partial class StrandedIntakeRecovery(
    IWolverineRuntime runtime,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<StrandedIntakeRecovery> logger) : BackgroundService
{
    private const int BatchSize = 500;

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-sent {Count} stranded intake event(s) captured before {Cutoff}")]
    private partial void LogRecovered(int count, DateTimeOffset cutoff);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stranded intake recovery failed; next attempt at the next interval")]
    private partial void LogFailed(Exception ex);

    /// <summary>Re-sends unprocessed, not dead-lettered raw events captured before now minus <paramref name="olderThan"/>.</summary>
    public async Task<int> RecoverAsync(TimeSpan olderThan, CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow() - olderThan;
        var stranded = new List<TwinIntakeCaptured>();
        await using (var conn = new SqlConnection(options.Value.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                $"""
                SELECT TOP ({BatchSize}) id, source, kind FROM twin.raw_intake_events
                WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND received_at < @cutoff
                ORDER BY sequence
                """, conn);
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stranded.Add(new TwinIntakeCaptured(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (stranded.Count == 0)
        {
            return 0;
        }

        var bus = new MessageContext(runtime);
        foreach (var message in stranded)
        {
            await bus.PublishAsync(message);
        }
        await bus.FlushOutgoingMessagesAsync();
        LogRecovered(stranded.Count, cutoff);
        return stranded.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ProcessorEnabled || settings.RecoverStrandedAfterMinutes <= 0)
        {
            return;
        }

        var olderThan = TimeSpan.FromMinutes(settings.RecoverStrandedAfterMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RecoverAsync(olderThan, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
    }
}
