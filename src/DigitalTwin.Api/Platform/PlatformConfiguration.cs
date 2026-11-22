using DigitalTwin.Core.Hierarchy;

namespace DigitalTwin.Api.Platform;

/// <summary>
/// Chooses the observed platform from configuration: <c>Twin:Platform:Json</c> (the definition inline, e.g. a Container App setting)
/// or <c>Twin:Platform:File</c> (a path to a *.platform.json). Without either, the twin observes cmdemo1.
/// </summary>
public static class PlatformConfiguration
{
    public static PlatformDefinition Load(IConfiguration configuration)
    {
        var json = configuration["Twin:Platform:Json"];
        if (!string.IsNullOrWhiteSpace(json))
        {
            return PlatformDefinition.FromJson(json);
        }

        var file = configuration["Twin:Platform:File"];
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                throw new InvalidOperationException($"Twin:Platform:File '{file}' does not exist (resolved to {Path.GetFullPath(file)}).");
            }
            return PlatformDefinition.FromJson(File.ReadAllText(file));
        }

        return PlatformDefinition.Cmdemo1();
    }
}
