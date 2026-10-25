namespace YogaITS.Tray.UI;

internal readonly record struct CaptureSurfaceSize(int Width, int Height);

internal static class CaptureSurfaceUpdates
{
    // Non-virtual Composition surfaces must be fully initialized before a
    // partial BeginDraw. Reset initialization whenever storage is resized.
    public static CaptureSurfaceSize Region(CaptureSurfaceSize capacity, DesktopCaptureViewport viewport, bool initialized) =>
        initialized ? new(viewport.PixelWidth, viewport.PixelHeight) : capacity;
}

// HWND dimensions are PHYSICAL pixels; Composition geometry is in XAML DIPs.
// Keep them independent so a pending XAML arrange cannot stretch the capture.
internal readonly record struct DesktopCaptureViewport(int PixelWidth, int PixelHeight, double Scale)
{
    public bool CanDraw => PixelWidth > 0 && PixelHeight > 0;
    public double WidthDip => PixelWidth / Scale;
    public double HeightDip => PixelHeight / Scale;
    public static DesktopCaptureViewport Create(int width, int height, double scale) => new(
        Math.Max(0, width), Math.Max(0, height), double.IsFinite(scale) && scale > 0 ? scale : 1);

    public CaptureSurfaceSize Capacity(CaptureSurfaceSize previous, bool resizing, int limit)
    {
        limit = Math.Max(1, limit);
        int Round(int size) => (int)Math.Min(limit, ((long)Math.Max(1, size) + 127) / 128 * 128);
        int width = Round(PixelWidth), height = Round(PixelHeight);
        // During edge dragging, grow in buckets and defer shrinking until exit.
        // This is ONLY the local drawing surface, never the WGC monitor pool.
        return resizing
            ? new(Math.Max(width, Math.Clamp(previous.Width, 1, limit)), Math.Max(height, Math.Clamp(previous.Height, 1, limit)))
            : new(width, height);
    }
}
