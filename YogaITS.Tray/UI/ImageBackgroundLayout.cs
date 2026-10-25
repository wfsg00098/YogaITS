namespace YogaITS.Tray.UI;

internal enum WallpaperPosition { Center, Tile, Stretch, Fit, Fill, Span }
internal readonly record struct ImageRect(double X, double Y, double Width, double Height);

internal static class ImageBackgroundLayout
{
    // Uniform-to-fill, centered: up/downscale equally on both axes, crop overflow.
    public static ImageRect Cover(double imageWidth, double imageHeight, ImageRect bounds) =>
        Place(imageWidth, imageHeight, bounds, WallpaperPosition.Fill);
    public static ImageRect Place(double imageWidth, double imageHeight, ImageRect bounds, WallpaperPosition position)
    {
        if (!double.IsFinite(imageWidth + imageHeight + bounds.X + bounds.Y + bounds.Width + bounds.Height) ||
            imageWidth <= 0 || imageHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (position == WallpaperPosition.Stretch) return bounds;
        double scale = position switch
        {
            WallpaperPosition.Center or WallpaperPosition.Tile => 1,
            WallpaperPosition.Fit => Math.Min(bounds.Width/imageWidth,bounds.Height/imageHeight),
            _ => Math.Max(bounds.Width/imageWidth,bounds.Height/imageHeight)
        };
        double width=imageWidth*scale, height=imageHeight*scale;
        return new(bounds.X+(bounds.Width-width)/2,bounds.Y+(bounds.Height-height)/2,width,height);
    }
}
