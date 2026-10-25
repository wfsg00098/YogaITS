namespace YogaITS.Tray.UI;

// One serial worker, one replaceable pending request. Producer/UI never waits
// for drawing or disposal. Cleanup runs after the in-flight request completes.
internal sealed class LatestWorkLoop<T> : IDisposable where T : class
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Action<T> _process;
    private readonly Action<Exception> _failed;
    private readonly Action _initialize, _cleanup;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private T? _latest;
    private bool _stopping;
    public Task Completion => _completion.Task;

    public LatestWorkLoop(Action<T> process, Action<Exception> failed, Action? initialize = null, Action? cleanup = null)
    {
        _process = process; _failed = failed;
        _initialize = initialize ?? (() => { }); _cleanup = cleanup ?? (() => { });
        var thread = new Thread(Run) { IsBackground = true, Name = "YogaITS LG background drawing" };
        thread.Start();
    }

    public bool Post(T request)
    {
        lock (_gate)
        {
            if (_stopping) return false;
            bool wake=_latest is null;
            _latest = request;
            if(wake) _ready.Set();
            return true;
        }
    }

    private void Run()
    {
        try
        {
            _initialize();
            while (true)
            {
                _ready.WaitOne();
                T? request;
                lock (_gate)
                {
                    if (_stopping) break;
                    request = _latest; _latest = null;
                }
                if (request is not null) _process(request);
            }
        }
        catch (Exception ex)
        {
            try { _failed(ex); } catch { /* Never let error reporting escape the worker. */ }
        }
        finally
        {
            lock (_gate) { _stopping = true; _latest = null; }
            try { _cleanup(); }
            catch (Exception ex) { try { _failed(ex); } catch { } }
            _ready.Dispose();
            _completion.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true; _latest = null; _ready.Set();
        }
    }
}