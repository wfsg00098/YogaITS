using YogaITS.Tray.UI.Glass.Capture;
using Windows.Graphics.Capture;

namespace YogaITS.Tray.UI.Glass.Runtime;

internal static class GlassCapability
{
    // Read-only checks: do not create a device/session or request consent here.
    private static readonly Lazy<bool> CaptureAvailable = new(() =>
    {
        try { return CaptureWindowExclusion.Supported && GraphicsCaptureSession.IsSupported(); }
        catch (Exception ex) { CaptureDiagnostics.Write("Glass capability: " + ex); return false; }
    });
    public static bool CaptureSupported => Supported && CaptureAvailable.Value;
    public static bool Supported
    {
        get
        {
            return BundledRuntime.GlassReady;
        }
    }
}
