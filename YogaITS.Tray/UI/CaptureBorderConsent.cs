using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace YogaITS.Tray.UI;

// Only Windows grants borderless capture. Never emulate approval in settings,
// change privacy registry values or suppress the border before explicit consent.
internal static class CaptureBorderConsent
{
    private static Task? _request;
    public static bool Allowed { get; private set; }
    public static string Status { get; private set; } = "尚未请求系统授权";
    public static bool Supported => ApiInformation.IsMethodPresent(
        "Windows.Graphics.Capture.GraphicsCaptureAccess", "RequestAccessAsync") &&
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");

    // Called on the UI dispatcher only when selecting transparent-desktop glass. All
    // windows/monitors share the request; system consent persists per app.
    public static Task RequestOnceAsync() => _request ??= RequestAsync();

    private static async Task RequestAsync()
    {
        try
        {
            if (!Supported) { Status = "系统不支持，保留捕获边框"; return; }
            string name;
            try { name = Package.Current.Id.Name; }
            catch (Exception ex)
            {
                Status = "尚未取得捕获身份，保留捕获边框";
                CaptureDiagnostics.Write($"Borderless capture identity unavailable: 0x{ex.HResult:X8}.");
                return;
            }
            if (name != "YogaITS")
            {
                Status = "捕获身份不匹配，保留捕获边框";
                return;
            }
            Status = "等待 Windows 授权";
            // CheckAccess never prompts. RequestAccessAsync presents the genuine
            // Windows consent UI when needed, and respects a previous denial.
            var capability = AppCapability.Create("graphicsCaptureWithoutBorder");
            var access = capability.CheckAccess();
            if (access != AppCapabilityAccessStatus.Allowed)
                access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Allowed = access == AppCapabilityAccessStatus.Allowed;
            Status = Allowed ? "Windows 已授权关闭捕获边框" : $"未授权（{access}），保留捕获边框";
        }
        catch (Exception ex)
        {
            Allowed = false;
            Status = $"授权不可用（0x{ex.HResult:X8}），保留捕获边框";
            CaptureDiagnostics.Write("Borderless capture consent: " + ex);
        }
        finally
        {
            CaptureDiagnostics.Write("Borderless capture: " + Status);
            // Publish only. The session owner applies it between two draws;
            // the UI continuation never synchronously sets IsBorderRequired.
            DesktopCaptureSource.RefreshBorderPreference();
        }
    }
}
