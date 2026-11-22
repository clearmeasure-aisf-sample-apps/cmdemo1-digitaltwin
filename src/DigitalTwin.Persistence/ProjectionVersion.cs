namespace DigitalTwin.Persistence;

/// <summary>Which state observation event each read-model entity (or replace-set scope) currently reflects.</summary>
public sealed class ProjectionVersion
{
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
}
