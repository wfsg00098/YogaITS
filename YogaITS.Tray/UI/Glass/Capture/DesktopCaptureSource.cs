using YogaITS.Tray.UI.Glass.Rendering;
using YogaITS.Tray.UI.Glass.Runtime;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace YogaITS.Tray.UI.Glass.Capture;

// UI publishes geometry/topology preferences. The renderer's single MTA thread
// owns EVERY live capture-session API call and all frame copy/drawing work.
internal sealed class DesktopCaptureSource : IDisposable
{
    private static readonly ConcurrentDictionary<DesktopCaptureSource, byte> Sources = new();
    private static readonly bool BorderSupported = CaptureBorderConsent.Supported;
    private readonly object _publishGate = new();
    private readonly IDirect3DDevice _directDevice;
    private readonly nint _directAbi;
    private readonly DirectXPixelFormat _format;
    private readonly LatestFrameSlot<Direct3D11CaptureFrame> _pendingFrame = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private readonly CaptureSnapshot _snapshot;
    private Direct3D11CaptureFrame? _heldFrame;
    private CanvasBitmap? _heldBitmap;
    private long _inputSequence;
    // One held frame, one replaceable pending frame, one producer buffer.
    private const int FramePoolCapacity=3;
    private volatile bool _running, _disposed, _activeRequested, _drawingAllowed;
    private nint[]? _requestedExclusions;
    private nint[] _excluded = [];
    private int _requestedBorder = 1, _appliedBorder = -1;
    private int _sessionThread, _arriving, _disposing;
    private int _captureWidth, _captureHeight;
    private bool _configured;
    private ulong _exclusionIteration;
    private long _nextOrderCheck, _sequence, _received, _published;
    private double _displayHz, _intervalMilliseconds;
    private string _stage = "Device created";
    public nint Monitor { get; }
    public nint Target { get; }
    public CanvasDevice Device { get; }
    public bool Hdr { get; }
    public double SdrWhiteScale { get; private set; } = 1;
    public string? Error { get; private set; }
    public event Action? FrameAvailable;
    public bool OrderCheckDue => Environment.TickCount64 >= Volatile.Read(ref _nextOrderCheck);
    public long PublishedFrames => Interlocked.Read(ref _published);
    public long CopiedFrames => Interlocked.Read(ref _sequence);
    public string Diagnostics => $"{(_running ? "捕获中" : "已暂停")} / {(Hdr ? "FP16 → SDR" : "SDR BGRA8")} / 收帧 {PublishedFrames} / 复制 {CopiedFrames} / 排除 {_excluded.Length} / Z 遍历 {CaptureWindowExclusion.QueryCount(Target)} / {_displayHz:F0} Hz / {_intervalMilliseconds:F1} ms";

    private DesktopCaptureSource(nint monitor, nint target)
    {
        Monitor = monitor; Target = target;
        _requestedBorder = CaptureBorderConsent.Allowed ? 0 : 1;
        Marshal.ThrowExceptionForHR(CreateCaptureDevice(monitor, out nint abi, out bool hdr));
        try
        {
            _directDevice = MarshalInterface<IDirect3DDevice>.FromAbi(abi);
            Device = CanvasDevice.CreateFromDirect3D11Device(_directDevice);
            _directAbi = abi;
        }
        catch { Marshal.Release(abi); throw; }
        Hdr = hdr;
        _format = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        _snapshot=new CaptureSnapshot(Device,_format);
    }

    public static DesktopCaptureSource Acquire(nint monitor, nint target)
    {
        var source = new DesktopCaptureSource(monitor, target);
        Sources.TryAdd(source, 0);
        return source;
    }

    // These UI methods only publish state. They never close/set a live WinRT
    // capture object, wait on a drawing lock, or wait for a worker completion.
    public void SetActive(bool active)
    {
        if (_disposed) return;
        _activeRequested = active;
        if (!active) _drawingAllowed = false;
    }

    public bool PrepareForDrawing()
    {
        _drawingAllowed = false;
        if (_disposed || !_activeRequested || !CaptureWindowExclusion.TrySnapshot(Target, out var windows)) return false;
        Volatile.Write(ref _requestedExclusions, windows);
        Volatile.Write(ref _nextOrderCheck, Environment.TickCount64 + 1000);
        _drawingAllowed = true;
        return true;
    }

    public void RequestStop() { _activeRequested = false; _drawingAllowed = false; _running = false; }

    public static void RefreshBorderPreference()
    {
        int required = CaptureBorderConsent.Allowed ? 0 : 1;
        foreach (var source in Sources.Keys)
        {
            Volatile.Write(ref source._requestedBorder, required);
            source.FrameAvailable?.Invoke(); // Queues work; never calls IsBorderRequired here.
        }
    }

    private void CheckSessionThread()
    {
        int current = Environment.CurrentManagedThreadId;
        if (_sessionThread == 0) _sessionThread = current;
        else if (_sessionThread != current)
            throw new InvalidOperationException("Capture session controls must remain on their owning drawing thread.");
    }

    private void ApplyExclusions(nint[] windows)
    {
        CheckSessionThread();
        if (_session is null || ReferenceEquals(_excluded,windows)) return;
        if(_configured && _excluded.AsSpan().SequenceEqual(windows)) { _excluded=windows; return; }
        _stage = "WindowOrder.Configure";
        ReleaseHeldFrame();
        _pendingFrame.Dispose(); // Return queued WGC buffers before changing the session.
        nint abi = MarshalInspectable<GraphicsCaptureSession>.FromManaged(_session);
        try
        {
            Marshal.ThrowExceptionForHR(SetCaptureWindowExclusions(abi, windows, (uint)windows.Length, out _exclusionIteration));
            _excluded = windows; _configured = true;
        }
        finally { Marshal.Release(abi); }
        CaptureDiagnostics.Write($"Window 0x{Target:X} exclusions applied on worker {_sessionThread}: count={windows.Length}, configuration={_exclusionIteration}.");
    }

    private void ApplyBorderPreference()
    {
        CheckSessionThread();
        int required = Volatile.Read(ref _requestedBorder);
        if (_session is null || !BorderSupported || _appliedBorder == required) return;
        ReleaseHeldFrame();
        _pendingFrame.Dispose();
        try
        {
            CaptureDiagnostics.Write($"Worker {_sessionThread} applying border preference: required={required}.");
            _session.IsBorderRequired = required != 0;
            _appliedBorder = required;
            CaptureDiagnostics.Write($"Worker {_sessionThread} border preference applied.");
        }
        catch (Exception ex)
        {
            // Cosmetic failure preserves capture and does not retry every frame.
            _appliedBorder = required;
            CaptureDiagnostics.Write($"Cannot apply capture border preference: 0x{ex.HResult:X8}.");
        }
    }

    private void Start(nint[] windows)
    {
        CheckSessionThread();
        _stage = "Capture.Start";
        if (!CaptureWindowExclusion.Supported || !GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Live glass requires Windows Graphics Capture with session-local exclusions.");
        RefreshSdrWhiteLevel();
        try
        {
            _item = CreateItem(Monitor);
            var size = _item.Size; _captureWidth = size.Width; _captureHeight = size.Height;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_directDevice, _format, FramePoolCapacity, size);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            nint abi = MarshalInspectable<GraphicsCaptureSession>.FromManaged(_session);
            try
            {
                int status = ConfigureCaptureTiming(abi, Monitor, out float hz, out float before, out float after);
                _displayHz = hz; _intervalMilliseconds = after;
                CaptureDiagnostics.Write($"Worker {_sessionThread} capture timing: hr=0x{status:X8}, display={hz:F2} Hz, interval={before:F3} -> {after:F3} ms.");
            }
            finally { Marshal.Release(abi); }
            _configured = false; _exclusionIteration = 0; _appliedBorder = -1;
            ApplyExclusions(windows); ApplyBorderPreference();
            _item.Closed += SourceClosed; _pool.FrameArrived += Arrived;
            _running = true; _session.StartCapture();
            CaptureDiagnostics.Write($"Worker {_sessionThread}: fixed full-monitor session started, {size.Width}x{size.Height}, HDR={Hdr}.");
        }
        catch { Stop(); throw; }
    }

    public void RefreshSdrWhiteLevel()
    {
        if (!Hdr || _disposed) return;
        int status = ReadCaptureSdrWhiteScale(Monitor, out float value);
        SdrWhiteScale = HdrToneMappingPolicy.WhiteScale(status >= 0 ? value : 1);
    }

    private void SourceClosed(GraphicsCaptureItem sender, object args)
    {
        if (!sender.Equals(_item)) return;
        Error = "Desktop capture source closed."; _running = false;
        FrameAvailable?.Invoke();
    }

    private void Arrived(Direct3D11CaptureFramePool sender, object args)
    {
        Interlocked.Increment(ref _received);
        if (!_running || !sender.Equals(_pool) || Interlocked.Exchange(ref _arriving, 1) != 0) return;
        Direct3D11CaptureFrame? newest = null;
        bool published = false;
        try
        {
            for (int index = 0; index < FramePoolCapacity; index++)
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) break;
                newest?.Dispose(); newest = frame;
            }
            if (newest is null) return;
            lock (_publishGate)
            {
                if (!_running || _disposed || !sender.Equals(_pool)) return;
                _pendingFrame.Publish(newest); newest = null; published = true;
                if (Interlocked.Increment(ref _published) == 1) CaptureDiagnostics.Write("First latest capture frame published.");
            }
        }
        catch (Exception ex)
        {
            if (_running && sender.Equals(_pool))
            {
                Error = "FrameArrived: " + ex; _running = false; published = true;
                CaptureDiagnostics.Write(Error);
            }
        }
        finally
        {
            newest?.Dispose(); Volatile.Write(ref _arriving, 0);
            if (published) FrameAvailable?.Invoke();
        }
    }

    // Called by DesktopCaptureRenderer's serial worker only. Controls always
    // execute between draws on the SAME MTA that created the capture session.
    public bool TryDrawLatest(Windows.Foundation.Rect crop,double sigma,Action<ICanvasImage,long,CaptureCopyRegion> draw)
    {
        CheckSessionThread();
        if (_disposed) return false;
        if (!_activeRequested) { Stop(); return false; }
        if (Error is not null) throw new InvalidOperationException(Error);
        var windows = Volatile.Read(ref _requestedExclusions);
        if (windows is null || !_drawingAllowed) return false;
        if (_session is null) Start(windows);
        else { ApplyExclusions(windows); ApplyBorderPreference(); }
        if(!UpdateSnapshot(crop,sigma)) return false;
        if (_heldBitmap is null || !_running || !_activeRequested || !_drawingAllowed) return false;
        draw(sigma>0?_snapshot.MonitorImage:_snapshot.Image,_sequence,_snapshot.Region);
        return true;
    }

    private bool UpdateSnapshot(Windows.Foundation.Rect crop,double sigma)
    {
        try
        {
            var newest=_pendingFrame.Take();
            if(newest is not null)
            {
                try
                {
                    if(_disposed) return false;
                    if(_configured)
                    {
                        nint abi=MarshalInspectable<Direct3D11CaptureFrame>.FromManaged(newest);
                        ulong iteration;
                        try { Marshal.ThrowExceptionForHR(ReadCaptureFrameIteration(abi,out iteration)); }
                        finally { Marshal.Release(abi); }
                        if(iteration<_exclusionIteration) return false;
                    }
                    var size=newest.ContentSize;
                    if(size.Width!=_captureWidth || size.Height!=_captureHeight)
                        throw new InvalidOperationException("Display capture size changed.");
                    var bitmap=CanvasBitmap.CreateFromDirect3D11Surface(Device,newest.Surface,96,CanvasAlphaMode.Ignore);
                    ReleaseHeldFrame();
                    _heldFrame=newest; newest=null; _heldBitmap=bitmap; _inputSequence++;
                }
                finally { newest?.Dispose(); }
            }
            if(_heldBitmap is null || _disposed) return false;
            var region=CaptureCopyRegion.Create(_captureWidth,_captureHeight,crop.X,crop.Y,crop.Width,crop.Height,sigma);
            _stage="Snapshot.CopyRegion";
            if(_snapshot.Copy(_heldBitmap,_inputSequence,region))
            {
                Marshal.ThrowExceptionForHR(FlushCaptureDevice(_directAbi));
                Interlocked.Increment(ref _sequence);
            }
            _stage="Snapshot.Ready";
            return true;
        }
        catch(Exception ex) { Error=_stage+": "+ex; throw new InvalidOperationException(Error,ex); }
    }
    private void ReleaseHeldFrame()
    {
        _heldBitmap?.Dispose(); _heldBitmap=null;
        _heldFrame?.Dispose(); _heldFrame=null;
    }

    private void Stop()
    {
        CheckSessionThread();
        _running = false;
        var pool = _pool; var item = _item; var session = _session;
        _pool = null; _item = null; _session = null;
        if (pool is not null) pool.FrameArrived -= Arrived;
        if (item is not null) item.Closed -= SourceClosed;
        ReleaseHeldFrame();
        try { session?.Dispose(); }
        finally
        {
            try { pool?.Dispose(); }
            finally { lock (_publishGate) _pendingFrame.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        RequestStop();
        try { Stop(); }
        finally
        {
            _disposed = true; Sources.TryRemove(this, out _); FrameAvailable = null;
            try { _snapshot.Dispose(); }
            finally
            {
                try { Device.Dispose(); }
                finally { try { _directDevice.Dispose(); } finally { Marshal.Release(_directAbi); } }
            }
        }
    }
    private static unsafe GraphicsCaptureItem CreateItem(nint monitor)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Guid interopId = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopId, out nint interop));
        nint item = 0;
        try
        {
            Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)interop)[4])(interop, monitor, &itemId, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally { if (item != 0) Marshal.Release(item); Marshal.Release(interop); }
    }

    [DllImport("YogaITS.DesktopCapture_lg.dll", ExactSpelling = true)]
    private static extern int ConfigureCaptureTiming(nint session, nint monitor, out float hz, out float beforeMilliseconds, out float afterMilliseconds);
    [DllImport("YogaITS.DesktopCapture_lg.dll", ExactSpelling = true)]
    private static extern int CreateCaptureDevice(nint monitor, out nint device, [MarshalAs(UnmanagedType.Bool)] out bool hdr);
    [DllImport("YogaITS.DesktopCapture_lg.dll", ExactSpelling = true)]
    private static extern int FlushCaptureDevice(nint device);
    [DllImport("YogaITS.DesktopCapture_lg.dll", ExactSpelling = true)]
    private static extern int ReadCaptureSdrWhiteScale(nint monitor, out float whiteScale);
    [DllImport("YogaITS.DesktopCapture_lg.dll", ExactSpelling = true)]
    private static extern int SetCaptureWindowExclusions(nint session, [In] nint[] windows, uint count, out ulong iteration);
    [DllImport("YogaITS.DesktopCapture_lg.dll", ExactSpelling = true)]
    private static extern int ReadCaptureFrameIteration(nint frame, out ulong iteration);
}
