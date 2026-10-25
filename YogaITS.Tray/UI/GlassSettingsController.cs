namespace YogaITS.Tray.UI;

// Latest user intent wins across the full and mini windows, including consent.
internal static class GlassSettingsController
{
    private static int _revision;
    private static Task? _initialStyle;
    private static bool _savedNative;
    public static void LoadSavedOptions()
    {
        _savedNative = Services.UserSettingsStore.Get("GlassNativeUI", false);
        var source = Services.UserSettingsStore.Get("GlassBackgroundSource", GlassBackgroundSource.DesktopWallpaper);
        if (Enum.IsDefined(source)) GlassBackgroundOptions.SetSource(source);
        string image = Services.UserSettingsStore.Get("GlassImagePath", "");
        if (!string.IsNullOrWhiteSpace(image))
        {
            try { GlassBackgroundOptions.SetImage(image); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { CaptureDiagnostics.Write("Saved image path: " + ex.Message); }
        }
    }
    public static Task EnsureInitialStyleAsync() => _initialStyle ??= SelectStyleAsync(_savedNative);
    public static async Task SelectStyleAsync(bool native)
    {
        int revision=++_revision;
        if(!native && !GlassCapability.Supported) return;
        if(GlassBackgroundOptions.NeedsCaptureConsent(native,GlassBackgroundOptions.Source))
            await CaptureBorderConsent.RequestOnceAsync();
        if(revision==_revision)
        {
            LiquidGlassWindow.SetNativeRequested(native);
            Services.UserSettingsStore.Set("GlassNativeUI", native);
        }
    }
    public static async Task SelectSourceAsync(GlassBackgroundSource source)
    {
        if(source==GlassBackgroundSource.TransparentDesktop && !GlassCapability.CaptureSupported) return;
        int revision=++_revision;
        if(GlassBackgroundOptions.NeedsCaptureConsent(LiquidGlassWindow.NativeRequested,source))
            await CaptureBorderConsent.RequestOnceAsync();
        if(revision!=_revision) return;
        GlassBackgroundOptions.SetSource(source);
        Services.UserSettingsStore.Set("GlassBackgroundSource", source);
        LiquidGlassWindow.RefreshBackground();
    }
    public static void SelectImage(string path)
    {
        ++_revision;
        GlassBackgroundOptions.SetImage(path);
        Services.UserSettingsStore.Set("GlassImagePath", GlassBackgroundOptions.ImagePath);
        LiquidGlassWindow.RefreshBackground();
    }
}
