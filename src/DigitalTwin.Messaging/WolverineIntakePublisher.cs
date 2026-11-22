using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace DigitalTwin.Messaging;

/// <summary>
/// Transactional-outbox intake: the raw row and the Wolverine envelope are committed in one SQL transaction.
/// Only after commit does the caller acknowledge the sender; any failure before commit surfaces as an exception.
/// </summary>
public sealed partial class WolverineIntakePublisher(
    IWolverineRuntime runtime,
    IOptions<MessagingOptions> options,
    ILogger<WolverineIntakePublisher> logger) : IIntakePublisher
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Intake {RawEventId} committed but outbox flush failed; Wolverine durability agent will deliver it")]
    private partial void LogFlushDeferred(Exception ex, Guid rawEventId);

    public async Task<IntakeReceipt> CaptureAsync(RawIntakeEvent rawEvent, CancellationToken ct)
    {
        var database = runtime.Storage as IMessageDatabase
            ?? throw new InvalidOperationException("Twin intake requires a SQL Server-backed Wolverine message store.");

        var bus = new MessageContext(runtime);
        int bodyBytes;
        await using (var conn = new SqlConnection(options.Value.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

            bodyBytes = await SqlRawIntakeStore.InsertAsync(conn, tx, rawEvent, ct);
            await bus.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, tx));
            await bus.PublishAsync(new TwinIntakeCaptured(rawEvent.Id, rawEvent.Source.ToString(), rawEvent.Kind.ToString()));

            await tx.CommitAsync(ct);
        }

        try
        {
            await bus.FlushOutgoingMessagesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFlushDeferred(ex, rawEvent.Id);
        }

        return new IntakeReceipt(rawEvent.Id, bodyBytes);
    }
}
