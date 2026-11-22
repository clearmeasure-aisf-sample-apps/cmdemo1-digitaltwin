using Azure.Core;
using Azure.Identity;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.ErrorHandling;
using Wolverine.SqlServer;

namespace DigitalTwin.Messaging;

public static class MessagingHostExtensions
{
    public static IHostApplicationBuilder AddTwinMessaging(this IHostApplicationBuilder builder)
    {
        var messaging = ReadOptions(builder.Configuration);

        builder.Services.Configure<MessagingOptions>(opts =>
        {
            builder.Configuration.GetSection(MessagingOptions.SectionName).Bind(opts);
            opts.ConnectionString = messaging.ConnectionString;
        });
        builder.Services.AddSingleton<SqlRawIntakeStore>();
        builder.Services.AddSingleton<IRawIntakeStore>(sp => sp.GetRequiredService<SqlRawIntakeStore>());
        builder.Services.AddSingleton<IIntakePublisher, WolverineIntakePublisher>();
        builder.Services.AddSingleton<WolverineIntakeQueueMonitor>();
        builder.Services.AddSingleton<IIntakeQueueMonitor>(sp => sp.GetRequiredService<WolverineIntakeQueueMonitor>());
        builder.Services.AddSingleton<IDeadLetterAdmin, WolverineDeadLetterAdmin>();

        builder.UseWolverine(opts =>
        {
            opts.ServiceName = "cmdemo1-digitaltwin";
            opts.Discovery.IncludeAssembly(typeof(TwinIntakeCapturedHandler).Assembly);
            ConfigureTransport(opts, messaging);

            opts.Durability.Mode = Enum.Parse<DurabilityMode>(messaging.DurabilityMode, ignoreCase: true);
            // Evaluated first: deferral (rebuild in progress) is not a failure and never dead-letters.
            opts.Policies.OnException<IntakeApplyDeferredException>().ScheduleRetryIndefinitely(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
            opts.Policies.OnAnyException().RetryWithCooldown([.. messaging.RetryDelaySchedule]).Then.MoveToErrorQueue();

        });

        return builder;
    }

    /// <summary>Creates the raw intake table; run after the model schema so EF EnsureCreated still sees an empty database.</summary>
    public static Task InitializeTwinMessagingAsync(this IServiceProvider services, CancellationToken ct = default)
        => services.GetRequiredService<SqlRawIntakeStore>().EnsureSchemaAsync(ct);

    private static void ConfigureTransport(WolverineOptions opts, MessagingOptions messaging)
    {
        var wolverineConnection = WithMinimumCommandTimeout(messaging.ConnectionString, messaging.WolverineCommandTimeoutSeconds);
        switch (messaging.Transport)
        {
            case IntakeTransport.LocalDurable:
                opts.PersistMessagesWithSqlServer(wolverineConnection, messaging.WolverineSchema);
                opts.PublishMessage<TwinIntakeCaptured>().ToLocalQueue(messaging.QueueName);
                // Sequential: live application order equals capture order, the order a rebuild replays in.
                opts.LocalQueue(messaging.QueueName).Sequential().UseDurableInbox();
                break;

            case IntakeTransport.SqlServer:
                opts.UseSqlServerPersistenceAndTransport(wolverineConnection, messaging.WolverineSchema).AutoProvision();
                opts.PublishMessage<TwinIntakeCaptured>().ToSqlServerQueue(messaging.SqlQueueName);
                if (messaging.ProcessorEnabled)
                {
                    // Bounded polling keeps load trivial on the small (Basic 5 DTU) twin database.
                    opts.ListenToSqlServerQueue(messaging.SqlQueueName)
                        .PollingInterval(TimeSpan.FromMilliseconds(messaging.SqlQueuePollingMs))
                        .Sequential()
                        .UseDurableInbox();
                }
                break;

            case IntakeTransport.AzureServiceBus:
                opts.PersistMessagesWithSqlServer(wolverineConnection, messaging.WolverineSchema);
                if (string.IsNullOrWhiteSpace(messaging.ServiceBusConnectionString))
                {
                    opts.UseAzureServiceBus(messaging.ServiceBusNamespace, CreateServiceBusCredential(messaging)).AutoProvision();
                }
                else
                {
                    // Shared access key (Bicep creates the queue) or emulator (queue in docker/servicebus-emulator/config.json): no provisioning.
                    opts.UseAzureServiceBus(messaging.ServiceBusConnectionString);
                }
                opts.PublishMessage<TwinIntakeCaptured>().ToAzureServiceBusQueue(messaging.QueueName).UseDurableOutbox();
                if (messaging.ProcessorEnabled)
                {
                    opts.ListenToAzureServiceBusQueue(messaging.QueueName).Sequential().UseDurableInbox();
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(messaging), messaging.Transport, "Unknown intake transport");
        }
    }

    private static string WithMinimumCommandTimeout(string connectionString, int minimumSeconds)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (builder.CommandTimeout < minimumSeconds)
        {
            builder.CommandTimeout = minimumSeconds;
        }
        return builder.ConnectionString;
    }

    private static TokenCredential CreateServiceBusCredential(MessagingOptions messaging)
        => string.IsNullOrWhiteSpace(messaging.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(messaging.ManagedIdentityClientId));

    private static MessagingOptions ReadOptions(IConfiguration configuration)
    {
        var messaging = new MessagingOptions();
        configuration.GetSection(MessagingOptions.SectionName).Bind(messaging);
        messaging.ConnectionString = new[]
        {
            messaging.ConnectionString,
            configuration.GetConnectionString("TwinDb"),
            configuration["TWIN_DB_CONNECTION_STRING"],
            configuration["Twin:ConnectionString"]
        }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(messaging.ConnectionString) ||
            messaging.ConnectionString.Contains(".db", StringComparison.OrdinalIgnoreCase) ||
            messaging.ConnectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Twin intake needs a SQL Server connection string (ConnectionStrings:TwinDb). For local runs start SQL Server in Docker; see README.");
        }
        if (messaging.Transport == IntakeTransport.AzureServiceBus &&
            string.IsNullOrWhiteSpace(messaging.ServiceBusNamespace) &&
            string.IsNullOrWhiteSpace(messaging.ServiceBusConnectionString))
        {
            throw new InvalidOperationException(
                "Twin:Messaging:ServiceBusNamespace (managed identity) or ServiceBusConnectionString (shared access key or emulator) is required for the AzureServiceBus transport.");
        }
        return messaging;
    }
}
