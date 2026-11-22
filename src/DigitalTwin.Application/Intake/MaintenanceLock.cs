namespace DigitalTwin.Application.Intake;

/// <summary>One maintenance operation at a time: a rebuild must never see retention move rows mid-replay.</summary>
public sealed class MaintenanceLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public string? Holder { get; private set; }

    public async Task<IDisposable?> TryEnterAsync(string holder, TimeSpan wait, CancellationToken ct)
    {
        if (!await _semaphore.WaitAsync(wait, ct))
        {
            return null;
        }
        Holder = holder;
        return new Releaser(this);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(MaintenanceLock owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Holder = null;
            owner._semaphore.Release();
        }
    }
}
