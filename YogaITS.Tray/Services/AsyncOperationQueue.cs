namespace YogaITS.Tray.Services;

// Writes keep their order and take priority over queued refreshes. Refresh
// bursts retain at most one running and one pending read, not an unbounded tail.
internal sealed class AsyncOperationQueue
{
    private sealed record Work(Func<Task> Operation, TaskCompletionSource Completion);
    private readonly object _sync = new();
    private readonly Queue<Work> _writes = new();
    private Work? _refresh;
    private bool _running;

    public Task Enqueue(Func<Task> operation, bool refresh = false)
    {
        lock (_sync)
        {
            if (refresh && _refresh is not null) return _refresh.Completion.Task;
            var work = new Work(operation, new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (refresh) _refresh = work;
            else _writes.Enqueue(work);
            if (!_running) { _running = true; _ = Task.Run(DrainAsync); }
            return work.Completion.Task;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Work work;
            lock (_sync)
            {
                if (_writes.TryDequeue(out var write)) work = write;
                else if (_refresh is { } read) { work = read; _refresh = null; }
                else { _running = false; return; }
            }
            try { await work.Operation().ConfigureAwait(false); work.Completion.TrySetResult(); }
            catch (OperationCanceledException ex) { work.Completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { work.Completion.TrySetException(ex); }
        }
    }
}
