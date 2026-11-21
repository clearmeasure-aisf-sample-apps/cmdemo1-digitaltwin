namespace DigitalTwin.Messaging;

public enum IntakeTransport
{
    /// <summary>In-process Wolverine durable local queue backed by the SQL Server message store (local dev, tests).</summary>
    LocalDurable,

    /// <summary>Wolverine SQL Server transport queue in the twin database (deployed twin today).</summary>
    SqlServer,

    /// <summary>Azure Service Bus queue with managed identity; outbox/inbox/dead letters stay in SQL Server.</summary>
    AzureServiceBus
}

public sealed class MessagingOptions
{
    public const string SectionName = "Twin:Messaging";

    public IntakeTransport Transport { get; set; } = IntakeTransport.LocalDurable;

    /// <summary>When false the host captures intake but does not process it (requires a queue transport).</summary>
    public bool ProcessorEnabled { get; set; } = true;

    public string ConnectionString { get; set; } = string.Empty;
    public string WolverineSchema { get; set; } = "wolverine";
    public string QueueName { get; set; } = "twin-intake";
    public int SqlQueuePollingMs { get; set; } = 1_000;
    public string ServiceBusNamespace { get; set; } = string.Empty;

    /// <summary>Connection string alternative to namespace + managed identity (local Service Bus emulator only).</summary>
    public string ServiceBusConnectionString { get; set; } = string.Empty;
    public string ManagedIdentityClientId { get; set; } = string.Empty;

    /// <summary>Comma-separated backoff (ms) between retries; after the last retry fails the message is dead-lettered.</summary>
    public string RetryDelays { get; set; } = "1000,5000,15000";

    /// <summary>Wolverine durability mode: Balanced (multi-node safe, default) or Solo (single node, faster recovery; tests).</summary>
    public string DurabilityMode { get; set; } = "Balanced";

    public IReadOnlyList<TimeSpan> RetryDelaySchedule
        => RetryDelays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ms => TimeSpan.FromMilliseconds(int.Parse(ms, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

    public int MaxAttempts => RetryDelaySchedule.Count + 1;

    /// <summary>SQL identifiers cannot contain '-', so the SQL Server transport queue uses underscores.</summary>
    public string SqlQueueName => QueueName.Replace('-', '_');
}
