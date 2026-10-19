namespace YogaITS.Tray.Services;

// Reads, writes and disposal share one gate; old refreshes cannot overtake writes.
internal abstract class SerializedFeatureService : IAsyncDisposable
{
    private readonly AsyncOperationQueue _queue = new();
    private bool _disposed;
    private Request? _activeRequest;
    public bool IsResponsive => Volatile.Read(ref _activeRequest)?.TimedOut != true;
    protected virtual TimeSpan ReadTimeout => TimeSpan.FromSeconds(10);
    protected virtual TimeSpan WriteTimeout => TimeSpan.FromSeconds(20);
    public event Action? Changed;
    protected void NotifyChanged() => Changed?.Invoke();
    private sealed class Request
    {
        private int _phase; // 0 queued, 1 running, 2 abandoned, 3 completed, 4 running but timed out.
        public bool TryStart() => Interlocked.CompareExchange(ref _phase, 1, 0) == 0;
        public bool AbandonPending() => Interlocked.CompareExchange(ref _phase, 2, 0) == 0;
        public bool MarkTimedOut() => Interlocked.CompareExchange(ref _phase, 4, 1) == 1;
        public bool TimedOut => Volatile.Read(ref _phase) == 4;
        public void Complete() => Volatile.Write(ref _phase, 3);
    }

    protected Task RunAsync(Func<Task> operation)
    {
        var request = new Request();
        return WaitBoundedAsync(QueueAsync(operation, false, request), false, request);
    }
    protected Task RefreshAsyncCore(Func<Task> operation)
    {
        var request = new Request();
        return WaitBoundedAsync(QueueAsync(operation, true, request), true, request);
    }

    private async Task WaitBoundedAsync(Task pending, bool refresh, Request request)
    {
        try { await pending.WaitAsync(refresh ? ReadTimeout : WriteTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (pending.IsCompleted) { await pending.ConfigureAwait(false); return; }
            if (request.AbandonPending())
            {
                if (!refresh) throw new TimeoutException("操作排队超时，尚未发送设置命令，已取消本次操作。");
                return;
            }
            if (!request.MarkTimedOut()) { await pending.ConfigureAwait(false); return; }
            NotifyChanged();
            // Keep ownership until the native call actually returns. Observing
            // its eventual failure must not retry it or release its live context.
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (!refresh) throw new TimeoutException("硬件服务响应超时，暂时禁用操作；原调用返回前不会并发重试。");
        }
    }

    private Task QueueAsync(Func<Task> operation, bool refresh, Request request) => _queue.Enqueue(async () =>
    {
        if (_disposed || !request.TryStart()) return;
        Volatile.Write(ref _activeRequest, request);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            request.Complete();
            Volatile.Write(ref _activeRequest, null);
            Changed?.Invoke();
        }
    }, refresh);

    protected virtual void Close() { }

    public ValueTask DisposeAsync() => new(_queue.Enqueue(() =>
    {
        if (!_disposed) { _disposed = true; Close(); }
        return Task.CompletedTask;
    }));
}
