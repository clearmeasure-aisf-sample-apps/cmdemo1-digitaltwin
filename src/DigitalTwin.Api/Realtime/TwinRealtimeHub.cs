using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Domain.Observation;
using Microsoft.AspNetCore.SignalR;

namespace DigitalTwin.Api.Realtime;

public sealed class TwinHub : Hub
{
}

public sealed record SseEnvelope(string EventName, string PayloadJson);

public sealed class RealtimeEventPublisher(IHubContext<TwinHub> hubContext) : IRealtimeEventPublisher
{
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

    public async Task PublishStateRefreshedAsync(string reason, DateTimeOffset at, CancellationToken ct)
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
