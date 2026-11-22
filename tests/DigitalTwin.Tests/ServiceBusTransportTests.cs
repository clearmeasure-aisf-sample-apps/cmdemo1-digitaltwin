using System.Net;
using DigitalTwin.Tests.Infrastructure;
using DotNet.Testcontainers.Builders;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;
using Xunit;

namespace DigitalTwin.Tests;

/// <summary>Runs only when TWIN_TEST_SERVICEBUS=1 (it pulls the Service Bus emulator and its SQL engine).</summary>
public sealed class ServiceBusFactAttribute : FactAttribute
{
    public ServiceBusFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TWIN_TEST_SERVICEBUS") != "1")
        {
            Skip = "Set TWIN_TEST_SERVICEBUS=1 to run the Azure Service Bus transport against the emulator.";
        }
    }
}

/// <summary>The Azure Service Bus intake transport, end to end against the emulator: no Azure subscription needed.</summary>
[Collection(SqlServerTests.Name)]
public sealed class ServiceBusTransportTests(SqlServerFixture sql)
{
    [Fact]
    public async Task TheServiceBusTransportIsSelectedByConfigurationAndValidatedAtStartup()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Messaging:Transport"] = "AzureServiceBus"
        });
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("ServiceBusNamespace", error.Message, StringComparison.Ordinal);
        Assert.Contains("ServiceBusConnectionString", error.Message, StringComparison.Ordinal);
    }

    [ServiceBusFact]
    public async Task WebhooksFlowThroughTheServiceBusQueue()
    {
        var config = Path.Combine(AppContext.BaseDirectory, "servicebus-emulator-config.json");
        // The emulator's default Azure SQL Edge engine is retired and crashes on current kernels: give it SQL Server 2022.
        await using var network = new NetworkBuilder().Build();
        const string password = "Emulator_Only_Pass1!";
        await using var emulatorSql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithNetwork(network)
            .WithNetworkAliases("emulator-sql")
            .WithPassword(password)
            .Build();
        await using var emulator = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .WithMsSqlContainer(network, emulatorSql, "emulator-sql", password)
            .WithConfig(config)
            .Build();
        await network.CreateAsync();
        await emulatorSql.StartAsync();
        await emulator.StartAsync();

        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Messaging:Transport"] = "AzureServiceBus",
            ["Twin:Messaging:ServiceBusConnectionString"] = emulator.GetConnectionString()
        });
        using var client = app.CreateAuthedClient();

        var response = await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "via-service-bus", """{"zen":"x","repository":{"name":"cmdemo1-workorders"}}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await TwinAppFactory.WaitUntilAsync(
            async () => await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey = 'github:via-service-bus'") == 1,
            TimeSpan.FromSeconds(90), "the webhook travelled through the Service Bus queue and was applied");
    }
}
