using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.UI;

namespace YogaITS.Tray.UI;

internal sealed record CaptureDrawRequest(DesktopCaptureViewport Viewport, Rect Crop, double Sigma,
    bool Resizing, Color ClearColor, double WhiteScale);

// Drawing can use the agile surface from the worker. Closing UI-created
// Composition objects still belongs to the UI compositor's owner thread.
internal sealed class DesktopCaptureRenderer : IDisposable
{
    private readonly DesktopCaptureSource _source;
    private readonly Action? _firstFrameReady;
    private readonly int _bitmapLimit;
    private readonly CompositionGraphicsDevice _graphics;
    private readonly LatestWorkLoop<CaptureDrawRequest> _worker;
    private CaptureDrawRequest? _request;
    private GaussianBlurEffect? _blur;
    private PixelShaderEffect? _toneMap;
    private CaptureSurfaceSize _capacity = new(1, 1);
    private bool _initialized, _apartmentInitialized;
    private long _lastFrame = -1, _drawn;
    private Rect _lastCrop;
    private double _lastSigma = double.NaN, _lastWhiteScale = double.NaN, _drawMilliseconds;
    private Color _lastColor;
    private string _stage = "Awaiting draw request";
    public CompositionDrawingSurface Surface { get; }
    public Task Completion { get; }
    public long Drawn => Interlocked.Read(ref _drawn);
    public double DrawMilliseconds => Volatile.Read(ref _drawMilliseconds);
    public string Diagnostics => $"容量 {_capacity.Width}×{_capacity.Height} / {_stage}";

    public DesktopCaptureRenderer(DesktopCaptureSource source, Compositor compositor, Action<Exception> failed, Action? firstFrameReady = null)
    {
        var owner=DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Capture rendering requires a UI resource owner.");
        _source = source; _firstFrameReady=firstFrameReady;
        _bitmapLimit=source.Device.MaximumBitmapSizeInPixels;
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, source.Device);
        try
        {
            Surface = _graphics.CreateDrawingSurface(new Size(1, 1),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            _worker = new(Draw, failed, Initialize, Cleanup);
            Completion=OwnerThreadRelease.AfterAsync(_worker.Completion,
                action=>owner.TryEnqueue(()=>action()), ()=>
                {
                    try { Surface.Dispose(); }
                    finally { _graphics.Dispose(); }
                }, failed);
        }
        catch { Surface?.Dispose(); _graphics.Dispose(); throw; }
    }

    public void Request(CaptureDrawRequest request)
    {
        Volatile.Write(ref _request, request);
        _worker.Post(request);
    }

    public void FrameAvailable()
    {
        var request = Volatile.Read(ref _request);
        if (request is not null) _worker.Post(request);
    }

    private void Initialize()
    {
        Marshal.ThrowExceptionForHR(RoInitialize(1)); // RO_INIT_MULTITHREADED, on this thread only.
        _apartmentInitialized = true;
    }

    private void Draw(CaptureDrawRequest request)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            _source.TryDrawLatest(request.Crop,request.Sigma,(frame, sequence,region) =>
            {
                var viewport = request.Viewport;
                int limit = _bitmapLimit;
                if (!viewport.CanDraw) return;
                if (viewport.PixelWidth > limit || viewport.PixelHeight > limit)
                    throw new InvalidOperationException("Window viewport exceeds the GPU drawing surface limit.");
                var capacity = viewport.Capacity(_capacity, request.Resizing, limit);
                if (_lastFrame == sequence && _lastCrop == request.Crop && _lastSigma == request.Sigma &&
                    _lastWhiteScale == request.WhiteScale && _lastColor == request.ClearColor && capacity == _capacity) return;
                _stage = "DrawingSurface.Resize";
                if (_capacity != capacity)
                {
                    Surface.Resize(new Windows.Graphics.SizeInt32(capacity.Width, capacity.Height));
                    _capacity = capacity; _initialized = false;
                }
                _blur ??= new GaussianBlurEffect { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Balanced,
                    BufferPrecision=_source.Hdr?CanvasBufferPrecision.Precision16Float:CanvasBufferPrecision.Precision8UIntNormalized };
                if(!ReferenceEquals(_blur.Source,frame)) _blur.Source=frame;
                if(_lastSigma!=request.Sigma) _blur.BlurAmount=(float)request.Sigma;
                ICanvasImage image = request.Sigma > 0 ? _blur : frame;
                if (_source.Hdr)
                {
                    _stage = "HDR.ToneMap";
                    if(_toneMap is null)
                    {
                        _toneMap=HdrToSdrEffect.Create();
                        _toneMap.BufferPrecision=CanvasBufferPrecision.Precision16Float;
                    }
                    if(!ReferenceEquals(_toneMap.Source1,image)) _toneMap.Source1=image;
                    if(_lastWhiteScale!=request.WhiteScale) _toneMap.Properties["WhiteScale"]=(float)request.WhiteScale;
                    image = _toneMap;
                }
                // WGC remains fixed to the full monitor. The owned processing
                // copy includes blur neighbours; restore its monitor-space origin
                // only when choosing this final window rectangle.
                _stage = "DrawingSurface.BeginDraw";
                var update = CaptureSurfaceUpdates.Region(_capacity, viewport, _initialized);
                using (var drawing = CanvasComposition.CreateDrawingSession(Surface, new Rect(0, 0, update.Width, update.Height), 96))
                {
                    drawing.Clear(request.ClearColor);
                    drawing.DrawImage(image, new Rect(0, 0, viewport.PixelWidth, viewport.PixelHeight),
                        request.Sigma>0?request.Crop:new Rect(request.Crop.X-region.X,request.Crop.Y-region.Y,request.Crop.Width,request.Crop.Height));
                    _stage = "DrawingSurface.EndDraw";
                }
                _initialized = true; _stage = "Worker.Ready";
                if (Interlocked.Increment(ref _drawn) == 1)
                {
                    CaptureDiagnostics.Write($"First worker HWND crop drawn: {request.Crop}, sigma={request.Sigma}.");
                    _firstFrameReady?.Invoke(); // EndDraw completed; never reveal a blank 1x1 surface.
                }
                _lastFrame = sequence; _lastCrop = request.Crop; _lastSigma = request.Sigma;
                _lastWhiteScale = request.WhiteScale; _lastColor = request.ClearColor;
            });
        }
        finally { Volatile.Write(ref _drawMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private void Cleanup()
    {
        Exception? failure = null;
        try
        {
            // Composition objects were created by the UI compositor. Their Close
            // happens on that owner only after this worker has stopped drawing.
            IDisposable?[] resources = [_toneMap, _blur, _source];
            foreach (var resource in resources)
            {
                try { resource?.Dispose(); }
                catch (Exception ex) { failure ??= ex; }
            }
        }
        finally { if (_apartmentInitialized) RoUninitialize(); }
        if (failure is not null) throw new InvalidOperationException("Drawing worker cleanup failed.", failure);
    }
    public void Dispose()
    {
        _source.RequestStop();
        Volatile.Write(ref _request, null);
        _worker.Dispose(); // Deferred cleanup, never Join/Wait on the UI thread.
    }

    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern void RoUninitialize();
}