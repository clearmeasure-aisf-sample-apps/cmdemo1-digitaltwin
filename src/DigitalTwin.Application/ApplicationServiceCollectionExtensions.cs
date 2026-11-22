using DigitalTwin.Application.Intake;
using DigitalTwin.Application.Services;
using DigitalTwin.Application.Workers;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddTwinApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.Configure<PollingOptions>(configuration.GetSection("Twin"));

        services.AddSingleton<ObservedEventIntake>();
        services.AddSingleton<StateObservationPublisher>();
        services.AddSingleton<ProjectionGate>();
        services.AddSingleton<ProjectionBaselineSeeder>();
        services.AddSingleton<DriftReconciliationService>();
        services.AddSingleton<TwinTimeTravel>();
        services.AddSingleton<TwinQueryService>();

        services.AddSingleton<IntakeEventApplier>();
        services.AddSingleton<IIntakeEventApplier>(sp => sp.GetRequiredService<IntakeEventApplier>());
        services.Configure<RetentionOptions>(configuration.GetSection("Twin:Retention"));
        services.AddSingleton<MaintenanceLock>();
        services.AddSingleton<RawIntakeHistory>();
        services.AddSingleton<RawIntakeRetentionService>();
        services.AddHostedService<RawIntakeRetentionHostedService>();
        services.AddSingleton<RebuildPreflight>();
        services.AddSingleton<IntakeRebuildService>();
        services.AddSingleton<FollowUpRefreshScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<FollowUpRefreshScheduler>());

        services.AddSingleton<IBackfillCoordinator, BackfillCoordinator>();
        services.AddHostedService<TwinPollingHostedService>();
        return services;
    }
}
