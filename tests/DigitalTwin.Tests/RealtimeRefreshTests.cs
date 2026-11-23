using DigitalTwin.Api.Realtime;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalTwin.Tests;

[Collection(SqlServerTests.Name)]
public sealed class RealtimeRefreshTests(SqlServerFixture sql)
{
    [Fact]
    public async Task ABurstOfStateRefreshesReachesViewersAsOneNowAndOneTrailing()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var publisher = app.Services.GetRequiredService<RealtimeEventPublisher>();
        var (id, reader) = publisher.SubscribeSse();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await publisher.PublishStateRefreshedAsync("state-observed", DateTimeOffset.UtcNow, default);
            }

            Assert.Equal(1, Drain());
            await Task.Delay(RealtimeEventPublisher.StateRefreshInterval + TimeSpan.FromSeconds(3));
            Assert.Equal(1, Drain());
        }
        finally
        {
            publisher.UnsubscribeSse(id);
        }

        int Drain()
        {
            var n = 0;
            while (reader.TryRead(out var envelope))
            {
                if (envelope.EventName == "stateRefreshed")
                {
                    n++;
                }
            }
            return n;
        }
    }
}
