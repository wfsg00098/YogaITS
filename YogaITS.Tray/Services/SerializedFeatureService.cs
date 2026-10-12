namespace YogaITS.Tray.Services;

// Reads, writes and disposal share one gate; old refreshes cannot overtake writes.
internal abstract class SerializedFeatureService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public event Action? Changed;
    protected void NotifyChanged() => Changed?.Invoke();

    protected Task RunAsync(Func<Task> operation) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
                await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    });

    protected virtual void Close() { }

    public virtual async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
        finally { _gate.Release(); }
    }
}
