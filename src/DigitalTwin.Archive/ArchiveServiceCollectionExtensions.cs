using Azure.Storage.Blobs;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Archive;

public static class ArchiveServiceCollectionExtensions
{
    public static IServiceCollection AddTwinArchive(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ArchiveOptions>(configuration.GetSection("Twin:Archive"));
        services.AddSingleton<IRawIntakeArchive>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
            return options.Provider switch
            {
                ArchiveProvider.AzureBlob when !string.IsNullOrWhiteSpace(options.ConnectionString)
                    => new BlobRawIntakeArchive(new BlobContainerClient(options.ConnectionString, options.Container)),
                ArchiveProvider.AzureBlob => new DisabledRawIntakeArchive(),
                ArchiveProvider.FileSystem => new FileSystemRawIntakeArchive(options.Path),
                ArchiveProvider.None => new DisabledRawIntakeArchive(),
                _ => throw new ArgumentOutOfRangeException(nameof(configuration), options.Provider, "Unknown archive provider")
            };
        });
        return services;
    }
}
