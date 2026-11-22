namespace DigitalTwin.Application.Intake;

/// <summary>Serializes live intake application with full rebuilds so a rebuild sees a quiescent model.</summary>
public sealed class ProjectionGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>How long live application waits for a rebuild before deferring; well below the handler execution timeout.</summary>
    public TimeSpan LiveWaitLimit { get; set; } = TimeSpan.FromSeconds(20);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        return new Releaser(_semaphore);
    }

    /// <summary>Waits at most <see cref="LiveWaitLimit"/>; null when the gate is still held (a rebuild is running).</summary>
    public async Task<IDisposable?> TryEnterLiveAsync(CancellationToken ct)
        => await _semaphore.WaitAsync(LiveWaitLimit, ct) ? new Releaser(_semaphore) : null;

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
