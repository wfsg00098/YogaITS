using YogaITS.Tray.UI.Glass.Backgrounds;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Windowing;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI.Glass.Capture;

// Fixed monitor capture -> GPU Gaussian over the full source -> local HWND crop
// -> real CompositionDrawingSurface -> glass. No offscreen VisualSurface mirror.
internal sealed class DesktopCaptureBackdrop(Window window, Grid root) : IDisposable
{
    public Canvas View { get; } = new() { IsHitTestVisible = false };
    private readonly SolidColorBrush _base = new();
    private readonly UISettings _settings = new();
    private DesktopCaptureSource? _source;
    private DesktopCaptureRenderer? _renderer;
    private readonly CaptureOperationState _captureOperation=new();
    private ImageBackgroundBrush? _imageBackground;
    private BackgroundCrossfade? _backgroundBlend;
    private BackgroundCrossfade.Layer? _inputLayer;
    private int _imageRevision=-1;
    private nint _imageMonitor;
    private RectI _sourceBounds;
    private CaptureDrawRequest? _lastDrawRequest;
    private GlassBackgroundSource _inputKind=GlassBackgroundSource.TransparentDesktop;
    public event Action? ContentChanged;
    public string? ImageError => _imageBackground?.Error;
    private CompositionSurfaceBrush? _desktop;
    private CompositionEffectFactory? _mixFactory;
    private CompositionEffectBrush? _mix;
    private SpriteVisual? _visibleSource;
    private XamlRoot? _xamlRoot;
    private SubclassProc? _subclass, _siteSubclass;
    private nint _hwnd, _registeredHwnd, _siteHwnd;
    private int _siteRepairs;
    private readonly GlassWindowFrame _windowFrame = new();
    private GlassResizeRegions? _resizeRegions;
    private bool _closed, _requested, _locked, _updating, _repairingTop, _resizing;
    private int _pending;
    private double _sigma = BackgroundTuning.DefaultBlurDip;
    private double _gaussianDip = BackgroundTuning.DefaultBlurDip;
    private DesktopCaptureViewport _viewport;
    private float _opacity = float.NaN;
    private string _stage = "Connect";
    private double _updateMilliseconds;
    private readonly CaptureFrameRateMeter _rates = new();
    private const nuint SiteSubclassId = 0x4C475349; // LGSI, only the owned XAML site.
    private const nuint SubclassId = 0x4C474350; // LGCP, private to this HWND.
    public string? Error { get; private set; }
    public bool CaptureRequested => _requested && GlassBackgroundOptions.Source==GlassBackgroundSource.TransparentDesktop;
    public CompositionBrush? SamplingBrush => _mix;
    public event Action? Failed;
    public string Diagnostics
    {
        get
        {
            var rates = _rates.Sample(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                _source?.PublishedFrames ?? 0, _source?.CopiedFrames ?? 0, _renderer?.Drawn ?? 0);
            return $"收帧 {rates.Published:F0} / 复制 {rates.Copied:F0} / 绘图 {rates.Drawn:F0} FPS · UI {_updateMilliseconds:F1} / 工作线程 {_renderer?.DrawMilliseconds ?? 0:F1} ms\n" +
                $"{_source?.Diagnostics ?? "来源未创建"}\n排除：{CaptureWindowExclusion.Status}\n无边框：{CaptureBorderConsent.Status}\n" +
                $"视口 {_viewport.PixelWidth}×{_viewport.PixelHeight} / {_renderer?.Diagnostics ?? _stage}\n{Error}";
        }
    }

    public void Connect()
    {
        _resizeRegions = new(window, root, SetResizing);
        View.Margin = new Thickness(-root.Padding.Left, -root.Padding.Top, -root.Padding.Right, -root.Padding.Bottom);
        Grid.SetRowSpan(View, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(View, Math.Max(1, root.ColumnDefinitions.Count));
        View.Background = _base;
        root.Children.Insert(0, View);
        var compositor = ElementCompositionPreview.GetElementVisual(View).Compositor;
        using var mix = new ArithmeticCompositeEffect
        {
            Name = "Mix", MultiplyAmount = 0, Offset = 0, Source1Amount = 0, Source2Amount = 1,
            Source1 = new CompositionEffectSourceParameter("Desktop"),
            Source2 = new ColorSourceEffect { Name = "Base", Color = Color.FromArgb(255, 32, 32, 32) }
        };
        _mixFactory = compositor.CreateEffectFactory(mix, ["Mix.Source1Amount", "Mix.Source2Amount", "Base.Color"]);
        _mix = _mixFactory.CreateBrush();
        var initial=root.ActualTheme==ElementTheme.Dark?Color.FromArgb(255,32,32,32):Color.FromArgb(255,243,243,243);
        _backgroundBlend=new(compositor,initial,action=>root.DispatcherQueue.TryEnqueue(()=>action()),
            brush=>_mix.SetSourceParameter("Desktop",brush));
        _mix.SetSourceParameter("Desktop", _backgroundBlend.Brush);
        // Render a REAL in-app visual under the glass, as in the verified
        // original-library baseline. Do not assume custom HLSL accepts a new
        // explicit nested effect/surface input in place of CreateBackdropBrush.
        _visibleSource = compositor.CreateSpriteVisual();
        _visibleSource.Brush = _mix;
        _visibleSource.RelativeSizeAdjustment = Vector2.Zero;
        // Keep the input opaque across the entire HWND rectangle. Rounding the
        // sampler before refraction exposes transparent/black corner pixels to
        // the glass. Only the glass output and DWM apply the rounded silhouette.
        ElementCompositionPreview.SetElementChildVisual(View, _visibleSource);
        CaptureDiagnostics.Write("Capture UI v16: capture creation, controls, border consent, drawing and disposal share one MTA worker; UI never waits.");
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        CaptureWindowExclusion.Register(_hwnd, () =>
        {
            if(GlassBackgroundOptions.Source==GlassBackgroundSource.TransparentDesktop) FrameAvailable();
        });
        _registeredHwnd = _hwnd;
        _subclass = WindowProc;
        if (!SetWindowSubclass(_hwnd, _subclass, SubclassId, 0))
        {
            Error = "Cannot attach synchronous capture crop tracking.";
            _subclass = null;
            return;
        }
        _ = WTSRegisterSessionNotification(_hwnd, 0);
        root.Loaded += Loaded;
        root.SizeChanged += SizeChanged;
        window.AppWindow.Changed += WindowChanged;
        if (root.IsLoaded) ConnectRoot();
    }

    public void Refresh(bool active, bool effectsEnabled = true, double gaussianDip = BackgroundTuning.DefaultBlurDip)
    {
        if (_closed) return;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        var color = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        if(_base.Color!=color) { _base.Color=color; _mix?.Properties.InsertColor("Base.Color",color); }
        _gaussianDip = double.IsFinite(gaussianDip) ? Math.Clamp(gaussianDip, 0, 100) : _gaussianDip;
        _requested = active && effectsEnabled;
        UpdateWindowFrame();
        Update();
    }

    private void UpdateWindowFrame()
    {
        if (_closed || _hwnd == 0) return;
        try
        {
            _windowFrame.SetEnabled(_hwnd, _requested);
            _resizeRegions?.Update(_requested);
        }
        catch (Exception ex)
        {
            if (Error is not null) return;
            Error = "Window contour: " + ex.Message;
            CaptureDiagnostics.Write(Error);
            Failed?.Invoke();
        }
    }

    private void Loaded(object sender, RoutedEventArgs args) => ConnectRoot();

    private void ConnectRoot()
    {
        if (_xamlRoot is not null) _xamlRoot.Changed -= RootChanged;
        _xamlRoot = root.XamlRoot;
        if (_xamlRoot is not null) _xamlRoot.Changed += RootChanged;
        RefreshGeometry();
        root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ReportBounds);
    }

    public void RefreshGeometry()
    {
        if (_closed || _hwnd == 0) return;
        GeometryChanged();
    }

    private void ReportBounds()
    {
        if (_closed || _hwnd == 0 || !GetClientRect(_hwnd, out var client)) return;
        nint bridge = FindWindowEx(_hwnd, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        var origin = new PointI();
        if (bridge == 0 || !ClientToScreen(_hwnd, ref origin) || !GetWindowRect(bridge, out var child)) return;
        _ = DwmGetWindowAttribute(_hwnd, 9, out var frame, Marshal.SizeOf<RectI>());
        CaptureDiagnostics.Write($"Capture layout: client={client.Right}x{client.Bottom}; " +
            $"frame=({frame.Left-origin.X},{frame.Top-origin.Y})-({frame.Right-origin.X},{frame.Bottom-origin.Y}); site repairs={_siteRepairs}; " +
            $"site=({child.Left-origin.X},{child.Top-origin.Y})-({child.Right-origin.X},{child.Bottom-origin.Y}); " +
            $"root={root.ActualWidth:F3}x{root.ActualHeight:F3} DIP; scale={root.XamlRoot?.RasterizationScale:F3}.");
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => GeometryChanged();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => GeometryChanged();
    private void WindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args) =>
        GeometryChanged(positionOnly:args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange && !args.DidVisibilityChange);

    private void GeometryChanged(bool positionOnly=false)
    {
        if (_closed || _hwnd == 0) return;
        UpdateWindowFrame();
        CoverTopRow();
        bool staticImage=_inputKind==GlassBackgroundSource.StaticImage &&
            GlassBackgroundOptions.Source==GlassBackgroundSource.StaticImage && _imageBackground is not null;
        // A window-relative bitmap moves with DWM. No crop, source rebinding,
        // property writes or dispatcher background update is needed on a move.
        if(staticImage && positionOnly) return;
        if(positionOnly && _requested && !_locked && _inputKind==GlassBackgroundSource.DesktopWallpaper &&
            GlassBackgroundOptions.Source==_inputKind && _imageBackground is not null &&
            _imageRevision==GlassBackgroundOptions.Revision && MonitorFromWindow(_hwnd,2)==_imageMonitor)
        {
            var origin=new PointI();
            if(ClientToScreen(_hwnd,ref origin))
                _imageBackground.Move(new(origin.X,origin.Y,_viewport.PixelWidth,_viewport.PixelHeight),_viewport.Scale);
            return; // Cached wallpaper: no dispatcher update, decode or blur.
        }
        if(positionOnly && !_updating && _requested && !_locked && _source is not null && _renderer is not null &&
            _inputKind==GlassBackgroundSource.TransparentDesktop && GlassBackgroundOptions.Source==_inputKind &&
            !_source.OrderCheckDue && _source.Error is null && MonitorFromWindow(_hwnd,2)==_source.Monitor)
        {
            DrawLatest(force:false);
            return; // Submit the newest crop directly; no dispatcher topology pass.
        }
        var previousViewport=_viewport;
        UpdateWindowFrame();
        // Update only cheap compositor bounds synchronously. GPU/D2D work runs
        // once after redundant WM_MOVE/WM_SIZE/AppWindow notifications merge.
        if (!_updating) UpdateViewport();
        if(staticImage && _viewport==previousViewport) return;
        if(_imageBackground is not null && GlassBackgroundOptions.Source!=GlassBackgroundSource.TransparentDesktop)
        {
            var origin=new PointI();
            if(ClientToScreen(_hwnd,ref origin))
                _imageBackground.Move(new(origin.X,origin.Y,_viewport.PixelWidth,_viewport.PixelHeight),_viewport.Scale);
        }
        FrameAvailable();
    }

    private void FrameAvailable()
    {
        // At most one dispatcher WAKE-UP, never a queue of captured frames.
        // The callback reads the newest snapshot at execution, not at enqueue.
        if (_closed || !_requested || Interlocked.Exchange(ref _pending, 1) != 0) return;
        if (!root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Volatile.Write(ref _pending, 0);
            if (!_closed) Update();
        })) Volatile.Write(ref _pending, 0);
    }

    private void Update()
    {
        if (_closed || _updating || _hwnd == 0) return;
        _updating = true;
        long started = Stopwatch.GetTimestamp();
        try
        {
            UpdateViewport();
            var request=GlassBackgroundOptions.Snapshot;
            var kind=request.Source;
            if(_inputKind!=kind)
            {
                ReleaseSource(); ReleaseImage();
                _inputKind=kind;
            }
            bool active = _requested && !_locked && !IsIconic(_hwnd) && _viewport.CanDraw && root.XamlRoot is not null && Error is null;
            if (active)
            {
                nint monitor = MonitorFromWindow(_hwnd, 2);
                if(kind==GlassBackgroundSource.TransparentDesktop)
                {
                    if (_source is not null && _source.Monitor != monitor) ReleaseSource();
                    if (_source is null) AcquireSource(monitor);
                    if (_source?.Error is { } error) throw new InvalidOperationException(error);
                    _source?.SetActive(true);
                    if (_source?.PrepareForDrawing() == true) DrawLatest();
                    if(_renderer?.Drawn>0 && _inputLayer is not null && request==GlassBackgroundOptions.Snapshot)
                        _backgroundBlend!.Present(_inputLayer,_settings.AnimationsEnabled);
                }
                else
                {
                    if(_imageBackground is null || _imageRevision!=request.Revision)
                    {
                        ReleaseImage();
                        var image=new ImageBackgroundBrush(_mix!.Compositor);
                        _imageBackground=image; _imageRevision=request.Revision;
                        image.Changed+=ImageChanged;
                        _inputLayer=_backgroundBlend!.Stage(image.Brush,image.Dispose);
                    }
                    var origin=new PointI();
                    if(ClientToScreen(_hwnd,ref origin))
                        _imageBackground.Refresh(request,_hwnd,monitor,new(origin.X,origin.Y,_viewport.PixelWidth,_viewport.PixelHeight),
                            _viewport.Scale,_gaussianDip,_base.Color);
                    _imageMonitor=monitor;
                    if(_imageBackground.HasContent && _inputLayer is not null && request==GlassBackgroundOptions.Snapshot)
                        _backgroundBlend!.Present(_inputLayer,_settings.AnimationsEnabled);
                }
            }
            else
            {
                _source?.SetActive(false);
                _renderer?.FrameAvailable(); // Apply pause on the worker even when normal drawing is hidden.
            }
            if(request!=GlassBackgroundOptions.Snapshot) FrameAvailable();
            Fade(active ? 1 : 0);
        }
        catch (Exception ex)
        {
            Error = _stage + ": " + ex;
            CaptureDiagnostics.Write(Error);
            ReleaseSource();
            Fade(0);
            System.Diagnostics.Debug.WriteLine("Desktop capture fallback: " + ex);
            Failed?.Invoke();
        }
        finally { _updateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; _updating = false; }
    }

    private void ImageChanged()
    {
        root.DispatcherQueue.TryEnqueue(() =>
        {
            if(_closed) return;
            FrameAvailable();
            ContentChanged?.Invoke();
        });
    }

    private void UpdateViewport()
    {
        if (!GetClientRect(_hwnd, out var client)) return;
        double scale = root.XamlRoot?.RasterizationScale ?? GetDpiForWindow(_hwnd) / 96.0;
        var viewport = DesktopCaptureViewport.Create(client.Right - client.Left, client.Bottom - client.Top, scale);
        if (_viewport != viewport)
        {
            _viewport = viewport;
            var logical = new Vector2((float)_viewport.WidthDip, (float)_viewport.HeightDip);
            if (_visibleSource is not null) _visibleSource.Size = logical;
            // One captured pixel maps to one physical output pixel. Unused
            // bucket capacity stays outside the visible visual/clip.
            if (_desktop is not null) _desktop.Scale = new Vector2((float)(1 / _viewport.Scale));
        }
        _sigma = Math.Clamp(_gaussianDip * _viewport.Scale, 0, 250);
    }

    private void AcquireSource(nint monitor)
    {
        _stage = "CaptureSource.Acquire";
        var source=DesktopCaptureSource.Acquire(monitor,_hwnd);
        _source=source;
        long operation=_captureOperation.Begin();
        DesktopCaptureRenderer? renderer=null;
        CompositionSurfaceBrush? desktop=null;
        try
        {
            var monitorInfo=new MonitorInfo {Size=(uint)Marshal.SizeOf<MonitorInfo>()};
            if(!GetMonitorInfo(monitor,ref monitorInfo)) throw new InvalidOperationException("Cannot read capture monitor bounds.");
            _sourceBounds=monitorInfo.Bounds;
            _stage="CaptureRenderer.Create";
            renderer=new DesktopCaptureRenderer(source,_mix!.Compositor,error=>
            {
                string message="Background drawing worker: "+error;
                CaptureDiagnostics.Write(message);
                root.DispatcherQueue.TryEnqueue(()=>_captureOperation.RunIfCurrent(operation,()=>
                {
                    if(_closed || !_requested || _inputKind!=GlassBackgroundSource.TransparentDesktop ||
                        !ReferenceEquals(_source,source)) return;
                    Error=message; Failed?.Invoke();
                }));
            },FrameAvailable);
            desktop=_mix.Compositor.CreateSurfaceBrush(renderer.Surface);
            desktop.Stretch=CompositionStretch.None;
            desktop.HorizontalAlignmentRatio=desktop.VerticalAlignmentRatio=0;
            desktop.Scale=new((float)(1/_viewport.Scale));
            var retainedRenderer=renderer; var retainedDesktop=desktop;
            _inputLayer=_backgroundBlend!.Stage(desktop,()=>
            {
                retainedDesktop.Surface=null; retainedDesktop.Dispose();
                source.RequestStop(); retainedRenderer.Dispose();
            });
            _renderer=renderer; _desktop=desktop;
            source.FrameAvailable+=SourceFrameAvailable;
        }
        catch
        {
            desktop?.Dispose(); source.RequestStop();
            if(renderer is not null) renderer.Dispose(); else source.Dispose();
            _source=null; throw;
        }
    }

    private void SourceFrameAvailable()
    {
        if (_closed || !_requested) return;
        var source = _source;
        if (source is null) return;
        // Capture frames go straight to the drawing worker. The dispatcher is
        // needed only for topology/configuration and the lazy safety recheck.
        if (source.Error is not null || source.OrderCheckDue) FrameAvailable();
        else _renderer?.FrameAvailable();
    }

    private void DrawLatest(bool force=true)
    {
        if(_source is null || _renderer is null || _hwnd==0 || !_viewport.CanDraw) return;
        var origin=new PointI();
        if(!ClientToScreen(_hwnd,ref origin)) return;
        var crop=new Rect(origin.X-_sourceBounds.Left,origin.Y-_sourceBounds.Top,_viewport.PixelWidth,_viewport.PixelHeight);
        var previous=_lastDrawRequest;
        if(!force && previous is not null && previous.Viewport==_viewport && previous.Crop==crop &&
            previous.Sigma==_sigma && previous.Resizing==_resizing && previous.ClearColor==_base.Color &&
            previous.WhiteScale==_source.SdrWhiteScale) return;
        _lastDrawRequest=new(_viewport,crop,_sigma,_resizing,_base.Color,_source.SdrWhiteScale);
        _renderer.Request(_lastDrawRequest);
    }
    private void Fade(float target)
    {
        if (_mix is null || _opacity == target) return;
        void Set(string property, float value)
        {
            if (_settings.AnimationsEnabled && float.IsFinite(_opacity))
            {
                using var animation = _mix.Compositor.CreateScalarKeyFrameAnimation();
                animation.Duration = TimeSpan.FromMilliseconds(200);
                animation.InsertKeyFrame(1, value);
                _mix.Properties.StartAnimation(property, animation);
            }
            else { _mix.Properties.StopAnimation(property); _mix.Properties.InsertScalar(property, value); }
        }
        Set("Mix.Source1Amount", target);
        Set("Mix.Source2Amount", 1 - target);
        _opacity = target;
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0082)
        {
            if (_subclass is not null) _ = RemoveWindowSubclass(hwnd, _subclass, SubclassId);
            _hwnd = 0;
        }
        nint result = _windowFrame.DefWindowProc(hwnd, message, wParam, lParam);
        if (message == 0x0084 && !_closed && _requested)
            return _windowFrame.HitTest(hwnd, lParam, result);
        if (_closed) return result;
        if (message == 0x0231) SetResizing(true); // WM_ENTERSIZEMOVE
        if (message == 0x0232) SetResizing(false); // WM_EXITSIZEMOVE
        if (message == 0x02B1) // WM_WTSSESSION_CHANGE
        {
            if (wParam == 7) _locked = true;
            if (wParam == 8) _locked = false;
            Update();
        }
        if (message == 0x0218 && wParam == 4) { _locked = true; Update(); } // suspend
        if (message == 0x0218 && wParam is 7 or 18) { _locked = false; Update(); }
        if (message == 0x007E) { _imageBackground?.InvalidateWallpaper(); ReleaseSource(); Update(); } // display/HDR format changed
        if (message == 0x001A) { _imageBackground?.InvalidateWallpaper(); _source?.RefreshSdrWhiteLevel(); Update(); } // read-only SDR white-level refresh
        if (message == 0x0005 && wParam == 1) Update(); // Minimize stops capture promptly.
        else if (message is 0x0003 or 0x0005 or 0x0047 or 0x02E0)
        {
            bool positionOnly=message==0x0003 || message==0x0047 && lParam!=0 &&
                (Marshal.PtrToStructure<WindowPosition>(lParam).Flags&0x1)!=0; // SWP_NOSIZE.
            GeometryChanged(positionOnly);
        }
        return result;
    }

    private void SetResizing(bool resizing)
    {
        if (_resizing == resizing) return;
        _resizing = resizing;
        GeometryChanged();
        if (!resizing) root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ReportBounds);
    }

    private void CoverTopRow()
    {
        if (_repairingTop || _hwnd == 0) return;
        EnsureSiteHook();
        nint bridge = _siteHwnd;
        var origin = new PointI();
        if (bridge == 0 || !GetClientRect(_hwnd, out var client) || !GetWindowRect(bridge, out var child) || !ClientToScreen(_hwnd, ref origin) ||
            !GlassEdgePolicy.HasReservedTopRow(client.Right, client.Bottom, child.Left - origin.X, child.Top - origin.Y, child.Right - origin.X, child.Bottom - origin.Y)) return;
        _repairingTop = true;
        try { _ = SetWindowPos(bridge, 0, 0, 0, client.Right, client.Bottom, 0x0014); }
        finally { _repairingTop = false; }
    }

    private void EnsureSiteHook()
    {
        if (_closed || _hwnd == 0 || _siteHwnd != 0) return;
        nint site = FindWindowEx(_hwnd, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        if (site == 0) return;
        _siteSubclass ??= ContentSiteProc;
        if (SetWindowSubclass(site, _siteSubclass, SiteSubclassId, 0)) _siteHwnd = site;
    }

    private nint ContentSiteProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0082)
        {
            if (_siteSubclass is not null) _ = RemoveWindowSubclass(hwnd, _siteSubclass, SiteSubclassId);
            if (_siteHwnd == hwnd) _siteHwnd = 0;
        }
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (_closed || !_requested || message != 0x0046 || lParam == 0 || _hwnd == 0) return result;
        // WinUI can reserve the row again during a move. Correct its proposed
        // position before Win32 applies it, instead of waiting for focus to change.
        if (!GetClientRect(_hwnd, out var client)) return result;
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        bool noMove = (position.Flags & 0x2) != 0, noSize = (position.Flags & 0x1) != 0;
        int x = position.X, y = position.Y, width = position.Width, height = position.Height;
        if (noMove || noSize)
        {
            if (!GetWindowRect(hwnd, out var current)) return result;
            if (noSize) { width = current.Right-current.Left; height = current.Bottom-current.Top; }
            if (noMove)
            {
                var origin = new PointI();
                if (!ClientToScreen(_hwnd, ref origin)) return result;
                x = current.Left-origin.X; y = current.Top-origin.Y;
            }
        }
        if (!GlassEdgePolicy.HasReservedTopRow(client.Right, client.Bottom, x, y, x+width, y+height)) return result;
        position.X = 0; position.Y = 0;
        position.Width = client.Right; position.Height = client.Bottom;
        // Do not turn a pure one-pixel move into an unnecessary XAML resize.
        position.Flags=(position.Flags&~0x3u) | (x==0 && y==0?0x2u:0) |
            (width==client.Right && height==client.Bottom?0x1u:0);
        Marshal.StructureToPtr(position, lParam, false);
        _siteRepairs++;
        return result;
    }

    private void ReleaseSource()
    {
        _captureOperation.Retire();
        var source=_source; var renderer=_renderer;
        _source=null; _renderer=null; _desktop=null; _lastDrawRequest=null;
        if(source is null) return;
        source.FrameAvailable-=SourceFrameAvailable;
        // Freeze the last valid drawing surface. The blend owns the producer
        // until its last visible branch has faded out, then disposes it on UI.
        source.SetActive(false); renderer?.FrameAvailable();
        _inputLayer=null;
        _backgroundBlend?.CancelPending();
    }
    private void ReleaseImage()
    {
        var image=_imageBackground; _imageBackground=null; _imageRevision=-1;
        if(image is null) return;
        image.Changed-=ImageChanged;
        _inputLayer=null;
        _backgroundBlend?.CancelPending();
    }

    public void Dispose()
    {
        if (_closed) return;
        _requested = false;
        try
        {
            _resizeRegions?.Dispose();
            if (_hwnd != 0) _windowFrame.SetEnabled(_hwnd, false);
        }
        catch (Exception ex) { CaptureDiagnostics.Write("Restore native window frame: " + ex); }
        _closed = true;
        root.Loaded -= Loaded; root.SizeChanged -= SizeChanged;
        if (_xamlRoot is not null) _xamlRoot.Changed -= RootChanged;
        window.AppWindow.Changed -= WindowChanged;
        if (_hwnd != 0)
        {
            _ = WTSUnRegisterSessionNotification(_hwnd);
            if (_subclass is not null) _ = RemoveWindowSubclass(_hwnd, _subclass, SubclassId);
        }
        if (_siteHwnd != 0 && _siteSubclass is not null)
            _ = RemoveWindowSubclass(_siteHwnd, _siteSubclass, SiteSubclassId);
        _siteHwnd = 0; _siteSubclass = null;
        _subclass = null; _hwnd = 0;
        ReleaseSource();
        ReleaseImage();
        if (_registeredHwnd != 0) CaptureWindowExclusion.Unregister(_registeredHwnd);
        _registeredHwnd = 0;
        ElementCompositionPreview.SetElementChildVisual(View, null);
        _visibleSource?.Dispose();
        _mix?.Dispose(); _backgroundBlend?.Dispose(); _mixFactory?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RectI value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public RectI Bounds, Work; public uint Flags; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref PointI point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out RectI value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out RectI value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
}
