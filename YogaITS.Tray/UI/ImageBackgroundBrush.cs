using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.UI;
using Windows.Storage;

namespace YogaITS.Tray.UI;

// Cached image/monitor scene below the existing glass. Dragging only changes
// wallpaper coordinates. Static images do no work for position-only moves.
internal sealed class ImageBackgroundBrush : IDisposable
{
    private readonly CanvasDevice _device=CanvasDevice.GetSharedDevice();
    private readonly CompositionGraphicsDevice _graphics;
    private readonly Func<nint,WallpaperSnapshot> _readWallpaper;
    private readonly CompositionSurfaceBrush _image;
    private readonly CompositionEffectFactory _factory;
    public CompositionEffectBrush Brush { get; }
    private CompositionDrawingSurface? _surface;
    private CanvasBitmap? _bitmap;
    private Scene? _requested, _applied;
    private int _loadRevision, _wallpaperRevision;
    private IAsyncOperation<CanvasBitmap>? _load;
    private bool _disposed, _wallpaperDirty=true;
    private nint _wallpaperMonitor;
    private WallpaperSnapshot? _wallpaper;
    private ImageRect _window;
    private double _scale=1, _sceneScale=1, _blurDip=double.NaN;
    private Vector2? _mappedScale, _mappedOffset;
    internal int MappingCommits { get; private set; }
    internal int WallpaperRenders { get; private set; }
    public bool HasContent=>_surface is not null && _applied is not null;
    public string? Error { get; private set; }
    public event Action? Changed;
    private sealed record Scene(GlassBackgroundSource Kind,string? Path,ImageRect Bounds,WallpaperPosition Position,Color Color,int Revision,int WallpaperRevision,double BlurPixels);
    public ImageBackgroundBrush(Compositor compositor,Func<nint,WallpaperSnapshot>? readWallpaper=null)
    {
        _readWallpaper=readWallpaper??DesktopWallpaperProvider.Read;
        _graphics=CanvasComposition.CreateCompositionGraphicsDevice(compositor,_device);
        _image=compositor.CreateSurfaceBrush(); _image.Stretch=CompositionStretch.None;
        _image.HorizontalAlignmentRatio=_image.VerticalAlignmentRatio=0;
        using var blur=new GaussianBlurEffect
        {
            Name="Blur", BlurAmount=1, BorderMode=EffectBorderMode.Hard,
            Source=new CompositionEffectSourceParameter("Image")
        };
        _factory=compositor.CreateEffectFactory(blur,["Blur.BlurAmount"]);
        Brush=_factory.CreateBrush(); Brush.SetSourceParameter("Image",_image);
    }
    public void InvalidateWallpaper() { _wallpaperDirty=true; _wallpaperRevision++; }
    public void Refresh(GlassBackgroundRequest request,nint hwnd,nint monitor,ImageRect window,double scale,double blurDip,Color baseColor)
    {
        if(_disposed) return;
        if(request.Source is not (GlassBackgroundSource.StaticImage or GlassBackgroundSource.DesktopWallpaper))
            throw new ArgumentOutOfRangeException(nameof(request),"An image backend requires a static image or wallpaper request.");
        _window=window; _scale=scale;
        var kind=request.Source;
        if(kind==GlassBackgroundSource.DesktopWallpaper && (_wallpaperDirty || _wallpaperMonitor!=monitor || _wallpaper is null))
        {
            _wallpaper=_readWallpaper(hwnd); _wallpaperMonitor=monitor; _wallpaperDirty=false;
        }
        var scene=kind==GlassBackgroundSource.StaticImage
            ? new Scene(kind,request.ImagePath,default,WallpaperPosition.Fill,baseColor,request.Revision,0,0)
            : new Scene(kind,_wallpaper?.Path,_wallpaper!.Bounds,_wallpaper.Position,_wallpaper.Color,request.Revision,_wallpaperRevision,blurDip*scale);
        double liveBlur=kind==GlassBackgroundSource.DesktopWallpaper?0:blurDip;
        if(_blurDip!=liveBlur) { _blurDip=liveBlur; Brush.Properties.InsertScalar("Blur.BlurAmount",(float)liveBlur); }
        if(_requested!=scene) { _requested=scene; _=LoadSceneAsync(scene); }
        Map();
    }
    private async Task LoadSceneAsync(Scene scene)
    {
        int revision=++_loadRevision;
        CancelLoad();
        CanvasBitmap? loaded=null;
        try
        {
            if(scene.Path is not null)
            {
                var file=await StorageFile.GetFileFromPathAsync(scene.Path);
                if(_disposed || revision!=_loadRevision) return;
                using var stream=await file.OpenReadAsync();
                if(_disposed || revision!=_loadRevision) return;
                var load=CanvasBitmap.LoadAsync(_device,stream,96);
                _load=load;
                try { loaded=await load; }
                finally
                {
                    if(ReferenceEquals(_load,load)) _load=null;
                    load.Close();
                }
                if(_disposed || revision!=_loadRevision) { loaded.Dispose(); return; }
            }
            if(_disposed || revision!=_loadRevision) return;
            _bitmap?.Dispose(); _bitmap=loaded; loaded=null;
            var size=new Size(_bitmap?.SizeInPixels.Width??1,_bitmap?.SizeInPixels.Height??1);
            double width=scene.Kind==GlassBackgroundSource.StaticImage?size.Width:scene.Bounds.Width;
            double height=scene.Kind==GlassBackgroundSource.StaticImage?size.Height:scene.Bounds.Height;
            double sceneScale=Math.Min(1,_device.MaximumBitmapSizeInPixels/Math.Max(width,height));
            int w=Math.Max(1,(int)Math.Ceiling(width*sceneScale)),h=Math.Max(1,(int)Math.Ceiling(height*sceneScale));
            var next=_graphics.CreateDrawingSurface(new Size(w,h),DirectXPixelFormat.B8G8R8A8UIntNormalized,DirectXAlphaMode.Premultiplied);
            try
            {
                void Paint(CanvasDrawingSession drawing)
                {
                    drawing.Clear(scene.Color);
                    drawing.Transform=Matrix3x2.CreateScale((float)sceneScale);
                    if(_bitmap is not null)
                    {
                        var target=scene.Kind==GlassBackgroundSource.StaticImage
                            ? new ImageRect(0,0,size.Width,size.Height)
                            : ImageBackgroundLayout.Place(size.Width,size.Height,new(0,0,width,height),scene.Position);
                        if(scene.Position==WallpaperPosition.Tile)
                        {
                            using var tiled=new BorderEffect { Source=_bitmap,ExtendX=CanvasEdgeBehavior.Wrap,ExtendY=CanvasEdgeBehavior.Wrap };
                            drawing.DrawImage(tiled,new Rect(0,0,width,height),new Rect(0,0,width,height));
                        }
                        else drawing.DrawImage(_bitmap,new Rect(target.X,target.Y,target.Width,target.Height),new Rect(0,0,size.Width,size.Height));
                    }
                }
                using(var drawing=CanvasComposition.CreateDrawingSession(next))
                {
                    if(scene.Kind==GlassBackgroundSource.DesktopWallpaper)
                    {
                        DrawCachedWallpaper(_device,drawing,w,h,(float)(scene.BlurPixels*sceneScale),Paint);
                        WallpaperRenders++;
                    }
                    else Paint(drawing);
                }
                var old=_surface; _surface=next; _applied=scene; _sceneScale=sceneScale; _image.Surface=next; old?.Dispose();
                _bitmap?.Dispose(); _bitmap=null; _load=null;
                Error=null; Map(); Changed?.Invoke();
            }
            catch { next.Dispose(); throw; }
        }
        catch(Exception ex)
        {
            loaded?.Dispose();
            if(_disposed || revision!=_loadRevision) return;
            Error=scene.Path is null?"无法读取桌面背景。":"无法载入图像，请重新选择。";
            CaptureDiagnostics.Write("Image background: "+ex);
            Changed?.Invoke();
        }
    }
    internal static void DrawCachedWallpaper(CanvasDevice device,CanvasDrawingSession destination,
        int width,int height,float sigma,Action<CanvasDrawingSession> paint)
    {
        if(sigma<=0) { paint(destination); return; }
        using var scene=new CanvasRenderTarget(device,width,height,96);
        using(var drawing=scene.CreateDrawingSession()) paint(drawing);
        using var blur=new GaussianBlurEffect { Source=scene,BlurAmount=sigma,BorderMode=EffectBorderMode.Hard };
        destination.DrawImage(blur);
    }
    public void Move(ImageRect window,double scale)
    {
        bool same=window.Width==_window.Width && window.Height==_window.Height && scale==_scale;
        bool positionIndependent=_applied?.Kind==GlassBackgroundSource.StaticImage;
        _window=window; _scale=scale;
        if(!same || !positionIndependent) Map();
    }
    private void Map()
    {
        if(_applied is null || _surface is null) return;
        Vector2 mappedScale,mappedOffset;
        if(_applied.Kind==GlassBackgroundSource.StaticImage)
        {
            var size=_surface.Size;
            var rect=ImageBackgroundLayout.Cover(size.Width,size.Height,new(0,0,_window.Width,_window.Height));
            mappedScale=new((float)(rect.Width/size.Width/_scale),(float)(rect.Height/size.Height/_scale));
            mappedOffset=new((float)(rect.X/_scale),(float)(rect.Y/_scale));
        }
        else
        {
            mappedScale=new((float)(1/(_scale*_sceneScale)));
            mappedOffset=new((float)((_applied.Bounds.X-_window.X)/_scale),(float)((_applied.Bounds.Y-_window.Y)/_scale));
        }
        bool changed=false;
        if(_mappedScale!=mappedScale) { _image.Scale=mappedScale; _mappedScale=mappedScale; changed=true; }
        if(_mappedOffset!=mappedOffset) { _image.Offset=mappedOffset; _mappedOffset=mappedOffset; changed=true; }
        if(changed) MappingCommits++;
    }
    private void CancelLoad()
    {
        var load=_load; _load=null;
        if(load is null) return;
        try { if(load.Status==AsyncStatus.Started) load.Cancel(); }
        catch(Exception ex) when(ex is COMException or InvalidOperationException)
        { System.Diagnostics.Debug.WriteLine("Image load already completed: "+ex.HResult); }
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _loadRevision++;
        CancelLoad();
        _image.Surface=null; _surface?.Dispose(); _bitmap?.Dispose();
        Brush.Dispose(); _factory.Dispose(); _image.Dispose(); _graphics.Dispose();
    }
}
