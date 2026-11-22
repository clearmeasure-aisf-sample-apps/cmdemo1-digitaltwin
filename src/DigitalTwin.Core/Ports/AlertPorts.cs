namespace DigitalTwin.Core.Ports;

public enum AlertKind
{
    DeployFailed,
    StalledChange,
    DeadLetters,
    ReconciliationDrift,
    Test
}

/// <summary>An alert the twin raises; <see cref="Key"/> identifies the condition so it is sent once.</summary>
public sealed record TwinAlert(AlertKind Kind, string Severity, string Key, string Title, string Text, string? Url, DateTimeOffset At);

/// <summary>Outgoing alert channel (an incoming-webhook URL for Teams/Slack). Off unless a URL is configured.</summary>
public interface IAlertSink
{
    bool IsEnabled { get; }
    string Format { get; }
    Task SendAsync(TwinAlert alert, CancellationToken ct);
}
