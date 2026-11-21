using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.GitHub;

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddTwinGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.Configure<GitHubOptions>(opts =>
        {
            opts.Token = FirstNonEmpty(configuration["GH_PAT"], configuration["GITHUB_TOKEN"], configuration["Twin:GitHubToken"]);
            opts.WebhookSecret = FirstNonEmpty(configuration["WEBHOOK_SECRET"], configuration["Twin:WebhookSecret"]);
        });

        services.AddSingleton<GitHubApiClient>();
        services.AddSingleton<IGitHubRateLimitProvider>(sp => sp.GetRequiredService<GitHubApiClient>());
        services.AddSingleton<IGitHubSyncService, GitHubSyncService>();
        return services;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
}
