using DigitalTwin.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.Probes;

public static class ProbesServiceCollectionExtensions
{
    public static IServiceCollection AddTwinHealthProbes(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<IHealthProbeService, HealthProbeService>();
        return services;
    }
}
