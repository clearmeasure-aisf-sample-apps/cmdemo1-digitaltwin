using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.Octopus;

public static class OctopusServiceCollectionExtensions
{
    public static IServiceCollection AddTwinOctopus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.Configure<OctopusOptions>(opts =>
        {
            opts.Url = FirstNonEmpty(configuration["OCTOPUS_URL"], configuration["Twin:OctopusUrl"], opts.Url);
            opts.ApiKey = FirstNonEmpty(configuration["OCTOPUS_API_KEY"], configuration["Twin:OctopusApiKey"]);
            opts.WebhookSecret = FirstNonEmpty(configuration["WEBHOOK_SECRET"], configuration["Twin:WebhookSecret"]);
        });
        services.AddSingleton<IOctopusSyncService, OctopusSyncService>();
        return services;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
}
