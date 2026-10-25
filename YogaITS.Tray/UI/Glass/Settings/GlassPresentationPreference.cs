namespace YogaITS.Tray.UI.Glass.Settings;

internal sealed class GlassPresentationPreference
{
    public bool NativeRequested { get; private set; } = true;
    public bool SetNativeRequested(bool native)
    {
        if (NativeRequested == native) return false;
        NativeRequested = native;
        return true;
    }

    public bool UseGlass(bool available) => available && !NativeRequested;
}
