using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.DirectX;

namespace YogaITS.Tray.UI.Glass.Capture;

// The entire bucket contains real monitor pixels, so raw bitmap alpha/border
// semantics match the old full-monitor snapshot without an extra crop effect.
internal sealed class CaptureSnapshot(CanvasDevice device,DirectXPixelFormat format) : IDisposable
{
    private CanvasRenderTarget? _surface;
    private long _inputSequence=-1;
    private readonly Transform2DEffect _monitorImage=new()
    {
        BufferPrecision=format==DirectXPixelFormat.R16G16B16A16Float?
            CanvasBufferPrecision.Precision16Float:CanvasBufferPrecision.Precision8UIntNormalized
    };
    private readonly int _limit=device.MaximumBitmapSizeInPixels;
    public CaptureCopyRegion Region { get; private set; }
    public ICanvasImage Image=>_surface??throw new InvalidOperationException("No capture snapshot has been copied.");
    public ICanvasImage MonitorImage=>_monitorImage;
    internal long Copies { get; private set; }
    internal long CopiedPixels { get; private set; }
    public bool Copy(CanvasBitmap input,long sequence,CaptureCopyRegion region)
    {
        if(_surface is not null && _inputSequence==sequence && Region==region) return false;
        if(region.Width>_limit || region.Height>_limit) throw new InvalidOperationException("Capture sample exceeds the GPU texture limit.");
        if(_surface is null || _surface.SizeInPixels.Width!=region.Width || _surface.SizeInPixels.Height!=region.Height)
        {
            var next=new CanvasRenderTarget(device,region.Width,region.Height,96,format,CanvasAlphaMode.Ignore);
            _monitorImage.Source=next;
            var old=_surface; _surface=next; old?.Dispose();
        }
        _surface.CopyPixelsFromBitmap(input,0,0,region.X,region.Y,region.Width,region.Height);
        if(Region.X!=region.X || Region.Y!=region.Y)
            _monitorImage.TransformMatrix=Matrix3x2.CreateTranslation(region.X,region.Y);
        Region=region; _inputSequence=sequence; Copies++; CopiedPixels+=region.Pixels;
        return true;
    }
    public void Dispose() { _monitorImage.Source=null; _monitorImage.Dispose(); _surface?.Dispose(); _surface=null; }
}
