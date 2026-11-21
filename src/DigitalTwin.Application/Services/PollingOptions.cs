namespace DigitalTwin.Application.Services;

public sealed class PollingOptions
{
    public bool EnableBackgroundWorkers { get; set; } = true;
    public int GitHubPollSeconds { get; set; } = 600;
    public int GitHubBoardPollSeconds { get; set; } = 300;
    public int OctopusPollSeconds { get; set; } = 45;
    public int AzurePollSeconds { get; set; } = 60;
    public int ProbeIntervalSeconds { get; set; } = 45;
}
