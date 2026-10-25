namespace YogaITS.Tray.UI;

internal enum GlassBackgroundSource { StaticImage, DesktopWallpaper, TransparentDesktop }
internal readonly record struct GlassBackgroundChoice(GlassBackgroundSource Source,string Label);
internal readonly record struct GlassBackgroundRequest(GlassBackgroundSource Source,string? ImagePath,int Revision);

// Shared by preview windows, without changing the user's desktop or saving files.
internal static class GlassBackgroundOptions
{
    public static GlassBackgroundSource Source { get; private set; } = GlassBackgroundSource.DesktopWallpaper;
    public static string? ImagePath { get; private set; }
    public static int Revision { get; private set; }
    public static GlassBackgroundRequest Snapshot=>new(Source,ImagePath,Revision);
    public static IReadOnlyList<GlassBackgroundChoice> Choices { get; }=Array.AsReadOnly<GlassBackgroundChoice>(
    [
        new(GlassBackgroundSource.StaticImage,"静态图像"),
        new(GlassBackgroundSource.DesktopWallpaper,"桌面壁纸"),
        new(GlassBackgroundSource.TransparentDesktop,"透明桌面")
    ]);
    public static int ChoiceIndex(GlassBackgroundSource source)
    {
        for(int i=0;i<Choices.Count;i++) if(Choices[i].Source==source) return i;
        throw new ArgumentOutOfRangeException(nameof(source));
    }
    public static bool NeedsCaptureConsent(bool native,GlassBackgroundSource source)=>
        !native && source==GlassBackgroundSource.TransparentDesktop;
    public static void SetSource(GlassBackgroundSource source)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (Source == source) return;
        Source = source; Revision++;
    }
    public static void SetImage(string path)
    {
        ImagePath = Path.GetFullPath(path);
        Revision++;
    }
}
