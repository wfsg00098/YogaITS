using YogaITS.Tray.UI.Glass.Backgrounds;
using YogaITS.Tray.UI.Glass.Capture;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Windowing;
using YogaITS.Tray.Services;

namespace YogaITS.Tray.UI.Glass.Settings;

// Latest user intent wins across the full and mini windows, including consent.
internal static class GlassSettingsController
{
    private static int _revision;
    private static int _imageRevision;
    private static Task? _initialStyle;
    private static bool _savedNative = true;
    private static string _savedImagePath = "";
    private static readonly BackgroundImageStore Images = new(Path.Combine(UserSettingsStore.DataDirectory, "Backgrounds"));

    public static void LoadSavedOptions()
    {
        _savedNative = UserSettingsStore.Get("GlassNativeUI", true);
        var source = UserSettingsStore.Get("GlassBackgroundSource", GlassBackgroundSource.DesktopWallpaper);
        if (Enum.IsDefined(source)) GlassBackgroundOptions.SetSource(source);
        _savedImagePath = UserSettingsStore.Get("GlassImagePath", "");
    }

    public static Task EnsureInitialStyleAsync() => _initialStyle ??= RestoreInitialOptionsAsync();

    private static async Task RestoreInitialOptionsAsync()
    {
        int revision = ++_revision;
        int imageRevision = _imageRevision;
        if (!string.IsNullOrWhiteSpace(_savedImagePath))
        {
            try
            {
                // Migrate older settings that still point outside our data folder.
                string image = await Images.ImportAsync(_savedImagePath);
                if (imageRevision == _imageRevision)
                {
                    if (!image.Equals(_savedImagePath, StringComparison.OrdinalIgnoreCase))
                        UserSettingsStore.Set("GlassImagePath", image);
                    GlassBackgroundOptions.SetImage(image);
                    LiquidGlassWindow.RefreshBackground();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { CaptureDiagnostics.Write("Saved image path: " + ex.Message); }
        }
        if (revision == _revision) await ApplyStyleAsync(_savedNative, revision, persist: false);
    }

    public static Task SelectStyleAsync(bool native) => ApplyStyleAsync(native, ++_revision, persist: true);

    private static async Task ApplyStyleAsync(bool native, int revision, bool persist)
    {
        if (!native && !GlassCapability.Supported) return;
        if (GlassBackgroundOptions.NeedsCaptureConsent(native, GlassBackgroundOptions.Source))
            await CaptureBorderConsent.RequestOnceAsync();
        if (revision != _revision) return;
        // Initial defaults are not a user selection. Preserve saved choices if
        // the renderer is temporarily unavailable, and do not rewrite on startup.
        if (persist) UserSettingsStore.Set("GlassNativeUI", native);
        LiquidGlassWindow.SetNativeRequested(native);
    }

    public static async Task SelectSourceAsync(GlassBackgroundSource source)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (source == GlassBackgroundSource.TransparentDesktop && !GlassCapability.CaptureSupported) return;
        int revision = ++_revision;
        if (GlassBackgroundOptions.NeedsCaptureConsent(LiquidGlassWindow.NativeRequested, source))
            await CaptureBorderConsent.RequestOnceAsync();
        if (revision != _revision) return;
        UserSettingsStore.Set("GlassBackgroundSource", source);
        GlassBackgroundOptions.SetSource(source);
        LiquidGlassWindow.RefreshBackground();
    }

    public static async Task SelectImageAsync(string path)
    {
        int revision = ++_imageRevision;
        string image = await Images.ImportAsync(path);
        if (revision != _imageRevision) return;
        UserSettingsStore.Set("GlassImagePath", image);
        GlassBackgroundOptions.SetImage(image);
        LiquidGlassWindow.RefreshBackground();
    }
}
