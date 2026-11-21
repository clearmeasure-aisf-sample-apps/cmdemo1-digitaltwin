using DigitalTwin.Application.Abstractions;
using DigitalTwin.Application.Services;
using DigitalTwin.Infrastructure.Clients;
using DigitalTwin.Infrastructure.Options;
using DigitalTwin.Infrastructure.Persistence;
using DigitalTwin.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDigitalTwinInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient();

        services.Configure<TwinOptions>(opts =>
        {
            configuration.GetSection("Twin").Bind(opts);
            opts.GitHubToken = FirstNonEmpty(configuration["GH_PAT"], configuration["GITHUB_TOKEN"], opts.GitHubToken);
            opts.OctopusUrl = FirstNonEmpty(configuration["OCTOPUS_URL"], opts.OctopusUrl);
            opts.OctopusApiKey = FirstNonEmpty(configuration["OCTOPUS_API_KEY"], opts.OctopusApiKey);
            opts.WebhookSecret = FirstNonEmpty(configuration["WEBHOOK_SECRET"], opts.WebhookSecret);
            opts.TwinApiKey = FirstNonEmpty(configuration["TWIN_API_KEY"], opts.TwinApiKey);
            opts.AzureClientId = FirstNonEmpty(configuration["AZURE_CLIENT_ID"], opts.AzureClientId);
            opts.AzureClientSecret = FirstNonEmpty(configuration["AZURE_CLIENT_SECRET"], opts.AzureClientSecret);
            opts.AzureTenantId = FirstNonEmpty(configuration["AZURE_TENANT_ID"], opts.AzureTenantId);
            opts.AzureSubscriptionId = FirstNonEmpty(configuration["AZURE_SUBSCRIPTION_ID"], opts.AzureSubscriptionId);
            opts.TwinPublicUrl = FirstNonEmpty(configuration["TWIN_PUBLIC_URL"], DeriveFromContainerAppHost(configuration["CONTAINER_APP_HOSTNAME"]), opts.TwinPublicUrl);

            var sqlConn = FirstNonEmpty(configuration.GetConnectionString("TwinDb"), configuration["TWIN_DB_CONNECTION_STRING"]);
            if (!string.IsNullOrWhiteSpace(sqlConn))
            {
                opts.ConnectionString = sqlConn;
                opts.UseSqlServer = sqlConn.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                                    sqlConn.Contains("Data Source=tcp:", StringComparison.OrdinalIgnoreCase);
            }
        });

        var connStr = FirstNonEmpty(
            configuration.GetConnectionString("TwinDb"),
            configuration["TWIN_DB_CONNECTION_STRING"],
            configuration["Twin:ConnectionString"],
            "Data Source=twin.db");

        var useSqlServer = connStr.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                           connStr.Contains("Data Source=tcp:", StringComparison.OrdinalIgnoreCase);

        services.AddDbContextFactory<TwinDbContext>(dbOpts =>
        {
            if (useSqlServer)
            {
                dbOpts.UseSqlServer(connStr, sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
            }
            else
            {
                dbOpts.UseSqlite(connStr);
            }
        });

        services.AddSingleton<ITwinStore, EfTwinStore>();
        services.AddSingleton<GitHubApiClient>();
        services.AddSingleton<IGitHubRateLimitProvider>(sp => sp.GetRequiredService<GitHubApiClient>());
        services.AddSingleton<EventIngestionService>();
        services.AddSingleton<TwinQueryService>();
        services.AddSingleton<IGitHubSyncService, GitHubSyncService>();
        services.AddSingleton<IOctopusSyncService, OctopusSyncService>();
        services.AddSingleton<IAzureSyncService, AzureSyncService>();
        services.AddSingleton<IHealthProbeService, HealthProbeService>();
        services.AddSingleton<IBackfillCoordinator, BackfillCoordinator>();
        services.AddHostedService<TwinPollingHostedService>();

        return services;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;

    private static string? DeriveFromContainerAppHost(string? hostname)
        => string.IsNullOrWhiteSpace(hostname) ? null : $"https://{hostname.Trim()}";
}
