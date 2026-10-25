namespace YogaITS.Tray.UI;

// One owned frame, never FIFO. Replacement releases the superseded frame;
// Take transfers ownership to the caller and empties the slot atomically.
internal sealed class LatestFrameSlot<T> : IDisposable where T : class, IDisposable
{
    private T? _latest;
    public void Publish(T frame) => Interlocked.Exchange(ref _latest, frame)?.Dispose();
    public T? Take() => Interlocked.Exchange(ref _latest, null);
    public void Dispose() => Take()?.Dispose();
}
