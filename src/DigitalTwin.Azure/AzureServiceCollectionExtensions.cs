using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.Azure;

public static class AzureServiceCollectionExtensions
{
    public static IServiceCollection AddTwinAzure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.Configure<AzureOptions>(opts =>
        {
            opts.ClientId = FirstNonEmpty(configuration["AZURE_CLIENT_ID"], configuration["Twin:AzureClientId"]);
            opts.ClientSecret = FirstNonEmpty(configuration["AZURE_CLIENT_SECRET"], configuration["Twin:AzureClientSecret"]);
            opts.TenantId = FirstNonEmpty(configuration["AZURE_TENANT_ID"], configuration["Twin:AzureTenantId"]);
            opts.SubscriptionId = FirstNonEmpty(configuration["AZURE_SUBSCRIPTION_ID"], configuration["Twin:AzureSubscriptionId"], opts.SubscriptionId);
            opts.WebhookSecret = FirstNonEmpty(configuration["WEBHOOK_SECRET"], configuration["Twin:WebhookSecret"]);
        });
        services.AddSingleton<IAzureSyncService, AzureSyncService>();
        return services;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
}
