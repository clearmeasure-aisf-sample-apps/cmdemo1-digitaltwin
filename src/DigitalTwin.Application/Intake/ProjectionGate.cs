namespace DigitalTwin.Application.Intake;

/// <summary>Serializes live intake application with full rebuilds so a rebuild sees a quiescent model.</summary>
public sealed class ProjectionGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
