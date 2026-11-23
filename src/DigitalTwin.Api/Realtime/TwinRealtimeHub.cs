using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Observation;
using Microsoft.AspNetCore.SignalR;

namespace DigitalTwin.Api.Realtime;

public sealed class TwinHub : Hub
{
}

public sealed record SseEnvelope(string EventName, string PayloadJson);

public sealed class RealtimeEventPublisher(IHubContext<TwinHub> hubContext, TimeProvider timeProvider) : IRealtimeEventPublisher
{
    /// <summary>
    /// Every open dashboard re-reads the whole dashboard state on "stateRefreshed". The applier signals after each applied
    /// observation, so during a burst of platform events the signal is coalesced to one per interval (plus a trailing one).
    /// </summary>
    public static readonly TimeSpan StateRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly Lock _refreshLock = new();
    private DateTimeOffset _lastRefreshSent = DateTimeOffset.MinValue;
    private bool _refreshScheduled;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ConcurrentDictionary<Guid, Channel<SseEnvelope>> _sseSubscribers = new();

    public async Task PublishEventAsync(ObservedEvent observedEvent, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(observedEvent, JsonOptions);
        await hubContext.Clients.All.SendAsync("twinEvent", observedEvent, ct);
        BroadcastSse(new SseEnvelope("twinEvent", json));
    }

    public Task PublishStateRefreshedAsync(string reason, DateTimeOffset at, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_refreshLock)
        {
            var now = timeProvider.GetUtcNow();
            wait = _lastRefreshSent + StateRefreshInterval - now;
            if (wait <= TimeSpan.Zero)
            {
                _lastRefreshSent = now;
            }
            else if (_refreshScheduled)
            {
                return Task.CompletedTask;
            }
            else
            {
                _refreshScheduled = true;
            }
        }

        if (wait <= TimeSpan.Zero)
        {
            return SendStateRefreshedAsync(reason, at, ct);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(wait, timeProvider);
            lock (_refreshLock)
            {
                _refreshScheduled = false;
                _lastRefreshSent = timeProvider.GetUtcNow();
            }
            await SendStateRefreshedAsync(reason, timeProvider.GetUtcNow(), CancellationToken.None);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task SendStateRefreshedAsync(string reason, DateTimeOffset at, CancellationToken ct)
    {
        var payload = new { reason, at };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await hubContext.Clients.All.SendAsync("stateRefreshed", payload, ct);
        BroadcastSse(new SseEnvelope("stateRefreshed", json));
    }

    public (Guid Id, ChannelReader<SseEnvelope> Reader) SubscribeSse()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<SseEnvelope>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _sseSubscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void UnsubscribeSse(Guid id)
    {
        if (_sseSubscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    private void BroadcastSse(SseEnvelope envelope)
    {
        foreach (var (_, ch) in _sseSubscribers)
        {
            ch.Writer.TryWrite(envelope);
        }
    }
}
