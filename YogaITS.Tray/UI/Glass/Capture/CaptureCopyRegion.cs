namespace YogaITS.Tray.UI.Glass.Capture;

internal readonly record struct CaptureCopyRegion(int X,int Y,int Width,int Height)
{
    public long Pixels=>(long)Width*Height;
    // WGC still captures one fixed full monitor. Only the owned processing copy
    // is cropped; four sigma plus interpolation guards preserve blur neighbours.
    public static CaptureCopyRegion Create(int monitorWidth,int monitorHeight,
        double x,double y,double width,double height,double sigma)
    {
        if(monitorWidth<=0 || monitorHeight<=0 || !double.IsFinite(x+y+width+height) || width<=0 || height<=0)
            throw new ArgumentOutOfRangeException(nameof(width));
        double halo=Math.Ceiling(Math.Clamp(double.IsFinite(sigma)?sigma:0,0,250)*4)+2;
        // Keep every copied texture pixel valid and retain bitmap border/alpha
        // semantics. Fixed-size buckets slide INWARD at monitor edges; clipping
        // the copy itself would change its extent on every drag pixel.
        int Round(double size,int limit)=>(int)Math.Min(limit,Math.Ceiling(size/128)*128);
        int w=Round(Math.Ceiling(width)+halo*2,monitorWidth);
        int h=Round(Math.Ceiling(height)+halo*2,monitorHeight);
        int left=(int)Math.Clamp(Math.Floor(x)-halo,0,monitorWidth-w);
        int top=(int)Math.Clamp(Math.Floor(y)-halo,0,monitorHeight-h);
        return new(left,top,w,h);
    }
}
