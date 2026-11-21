using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;
using Wolverine;

namespace DigitalTwin.Messaging;

/// <summary>Wolverine processor: applies each captured raw event to the model through the application port.</summary>
public static class TwinIntakeCapturedHandler
{
    public static async Task Handle(
        TwinIntakeCaptured message,
        Envelope envelope,
        IIntakeEventApplier applier,
        IRawIntakeStore rawStore,
        IOptions<MessagingOptions> options,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        try
        {
            await applier.ApplyAsync(message.RawEventId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var finalAttempt = envelope.Attempts >= options.Value.MaxAttempts;
            await rawStore.RecordFailureAsync(
                message.RawEventId,
                $"{ex.GetType().Name}: {ex.Message}",
                deadLettered: finalAttempt,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            throw;
        }
    }
}
