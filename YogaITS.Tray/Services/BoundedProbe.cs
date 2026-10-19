namespace YogaITS.Tray.Services;

internal readonly record struct ProbeResult<T>(bool Success, T Value);

// A timeout abandons waiting, never starts a replacement over a live native
// call. One hung driver/WMI probe therefore cannot leak a new worker per refresh.
internal sealed class BoundedProbe<T>
{
    private readonly object _sync = new();
    private Task<ProbeResult<T>>? _pending;
    private bool _abandoned;

    public async Task<ProbeResult<T>> ReadAsync(Func<T> read, TimeSpan? timeout = null)
    {
        Task<ProbeResult<T>> pending;
        lock (_sync)
        {
            if (_pending is not null && _abandoned)
            {
                if (!_pending.IsCompleted) return new(false, default!);
                _pending = null;
                _abandoned = false; // Never publish a late result from before a user mutation.
            }
            _pending ??= Task.Run(() =>
            {
                try { return new ProbeResult<T>(true, read()); }
                catch (Exception) { return new ProbeResult<T>(false, default!); }
            });
            pending = _pending;
        }
        try { return await pending.WaitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync) if (ReferenceEquals(_pending, pending)) _abandoned = true;
            return new(false, default!);
        }
        finally
        {
            lock (_sync)
                if (ReferenceEquals(_pending, pending) && pending.IsCompleted && !_abandoned) _pending = null;
        }
    }
}
