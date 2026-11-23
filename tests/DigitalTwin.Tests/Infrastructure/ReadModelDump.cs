using System.Text.Json;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Tests.Infrastructure;

/// <summary>Every read model of a twin read model as JSON, for comparing live tables and point-in-time folds.</summary>
public static class ReadModelDump
{
    public static async Task<Dictionary<string, string>> DumpAsync(ITwinReadModel model)
    {
        string J<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(rows, IntakeJson.Options);
        return new Dictionary<string, string>
        {
            ["repositories"] = J(await model.GetRepositoriesAsync(default)),
            ["sourceChanges"] = J(await model.GetSourceChangesAsync(default)),
            ["runs"] = J(await model.GetPipelineRunsAsync(int.MaxValue, default)),
            ["artifacts"] = J(await model.GetArtifactsAsync(default)),
            ["workItems"] = J(await model.GetWorkItemsAsync(default)),
            ["releases"] = J(await model.GetReleasesAsync(int.MaxValue, default)),
            ["deliveries"] = J(await model.GetDeliveriesAsync(int.MaxValue, default)),
            ["runbooks"] = J(await model.GetRunbooksAsync(int.MaxValue, default)),
            ["freezes"] = J(await model.GetFreezesAsync(default)),
            ["desired"] = JsonSerializer.Serialize(await model.GetDesiredConfigAsync(default), IntakeJson.Options),
            ["environments"] = J(await model.GetEnvironmentInstancesAsync(default)),
            ["workloads"] = J(await model.GetWorkloadsAsync(default)),
            ["azure"] = J(await model.GetAzureResourcesAsync(default)),
            ["probes"] = J(await model.GetHealthProbesAsync(default)),
            ["drift"] = J(await model.GetDriftFindingsAsync(includeResolved: true, default)),
            ["webhooks"] = J(await model.GetWebhookStatusesAsync(default))
        };
    }
}
