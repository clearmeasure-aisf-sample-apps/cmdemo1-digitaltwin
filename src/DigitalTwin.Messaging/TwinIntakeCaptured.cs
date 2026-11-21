namespace DigitalTwin.Messaging;

/// <summary>
/// Queue message referencing a raw intake row committed in the same SQL transaction.
/// Kept small so the same contract works on SQL Server and Azure Service Bus transports.
/// </summary>
public sealed record TwinIntakeCaptured(Guid RawEventId, string Source, string Kind);
