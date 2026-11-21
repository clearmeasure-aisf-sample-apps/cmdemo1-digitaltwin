using System.Text.Json;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;

namespace DigitalTwin.Messaging;

/// <summary>Queue depth, oldest-pending age and dead-letter visibility, transport-agnostic via the raw table and Wolverine store.</summary>
public sealed class WolverineIntakeQueueMonitor(
    IWolverineRuntime runtime,
    IRawIntakeStore rawStore,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider) : IIntakeQueueMonitor, IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, IntakeQueueStatus Status)? _cached;

    public async Task<IntakeQueueStatus> GetStatusAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (_cached is { } hit && now - hit.At < CacheFor)
        {
            return hit.Status;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var status = await ComputeAsync(now, ct);
            _cached = (now, status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    public void Dispose() => _gate.Dispose();

    private async Task<IntakeQueueStatus> ComputeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var transport = options.Value.Transport.ToString();
        try
        {
            var raw = await rawStore.GetStatsAsync(ct);
            var counts = await runtime.Storage.Admin.FetchCountsAsync();
            return new IntakeQueueStatus(
                Transport: transport,
                ProcessorEnabled: options.Value.ProcessorEnabled,
                Depth: raw.Pending,
                OldestPendingReceivedAt: raw.OldestPendingReceivedAt,
                OldestPendingAgeSeconds: raw.OldestPendingReceivedAt is { } oldest ? Math.Round((now - oldest).TotalSeconds, 1) : null,
                DeadLetterCount: Math.Max(counts.DeadLetter, raw.DeadLettered),
                CapturedTotal: raw.Total,
                ProcessedTotal: raw.Processed,
                LastCapturedAt: raw.LastCapturedAt,
                LastProcessedAt: raw.LastProcessedAt,
                TransportIncoming: counts.Incoming,
                TransportScheduled: counts.Scheduled,
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IntakeQueueStatus(transport, options.Value.ProcessorEnabled, 0, null, null, 0, 0, 0, null, null, 0, 0, ex.Message);
        }
    }
}

public sealed class WolverineDeadLetterAdmin(IWolverineRuntime runtime, WolverineIntakeQueueMonitor monitor) : IDeadLetterAdmin
{
    public async Task<IReadOnlyList<DeadLetteredIntake>> ListAsync(int limit, CancellationToken ct)
    {
        var results = await runtime.Storage.DeadLetters.QueryAsync(
            new DeadLetterEnvelopeQuery { PageSize = Math.Clamp(limit, 1, 500), Archived = DeadLetterArchiveFilter.Exclude },
            ct);
        return results.Envelopes.Select(ToSummary).ToList();
    }

    public async Task<int> ReplayAsync(IReadOnlyCollection<Guid>? envelopeIds, CancellationToken ct)
    {
        var ids = envelopeIds is { Count: > 0 }
            ? envelopeIds.ToArray()
            : (await ListAsync(500, ct)).Select(d => d.EnvelopeId).ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await runtime.Storage.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(ids);
        monitor.Invalidate();
        return ids.Length;
    }

    private static DeadLetteredIntake ToSummary(DeadLetterEnvelope dl)
        => new(
            EnvelopeId: dl.Id,
            RawEventId: TryReadRawEventId(dl),
            MessageType: dl.MessageType,
            ExceptionType: dl.ExceptionType,
            ExceptionMessage: dl.ExceptionMessage,
            FailedAt: dl.FailedAt ?? dl.SentAt,
            Replayable: dl.Replayable);

    private static Guid? TryReadRawEventId(DeadLetterEnvelope dl)
    {
        if (dl.Message is TwinIntakeCaptured captured)
        {
            return captured.RawEventId;
        }
        try
        {
            var data = dl.Envelope?.Data;
            return data is { Length: > 0 }
                ? JsonSerializer.Deserialize<TwinIntakeCaptured>(data, IntakeJson.Options)?.RawEventId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
