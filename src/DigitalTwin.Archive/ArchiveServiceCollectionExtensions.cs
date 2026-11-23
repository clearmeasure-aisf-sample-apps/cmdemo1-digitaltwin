using Azure.Storage.Blobs;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Archive;

public static class ArchiveServiceCollectionExtensions
{
    public static IServiceCollection AddTwinArchive(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ArchiveOptions>(configuration.GetSection("Twin:Archive"));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
            return new ConfiguredArchiveFiles(options.Provider switch
            {
                ArchiveProvider.AzureBlob when !string.IsNullOrWhiteSpace(options.ConnectionString)
                    => new BlobArchiveFiles(new BlobContainerClient(options.ConnectionString, options.Container)),
                ArchiveProvider.AzureBlob => null,
                ArchiveProvider.FileSystem => new FileSystemArchiveFiles(options.Path),
                ArchiveProvider.None => null,
                _ => throw new ArgumentOutOfRangeException(nameof(configuration), options.Provider, "Unknown archive provider")
            });
        });
        services.AddSingleton<IRawIntakeArchive>(sp => sp.GetRequiredService<ConfiguredArchiveFiles>().Files is { } files ? new RawIntakeArchive(files) : new DisabledRawIntakeArchive());
        services.AddSingleton<IJournalArchive>(sp => sp.GetRequiredService<ConfiguredArchiveFiles>().Files is { } files ? new JournalArchive(files) : new DisabledJournalArchive());
        return services;
    }
}

/// <summary>The archive files in use, or null when no archive is configured.</summary>
public sealed record ConfiguredArchiveFiles(IArchiveFiles? Files);

