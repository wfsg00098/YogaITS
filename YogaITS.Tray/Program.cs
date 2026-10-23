using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color.LenovoTcon;

namespace YogaITS.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A reset helper may still own the display stack; do not create WinUI resources yet.
        try { GpuDeviceRestartProtocol.WaitForIdle(TimeSpan.FromSeconds(45)); }
        catch (Exception ex) { GpuDeviceRestartProtocol.ShowError(ex.Message); return; }
        // Match the final probe: process security precedes UI/OLE/COM proxies.
        ProcessComSecurity.InitializeEarly();
        try
        {
            // Keep the UI on its existing/power-saving GPU and the dedicated
            // keep-awake executable on the high-performance GPU.
            // Windows can still require a subsequent launch to apply a new preference.
            AppGpuPreference.TrySetPowerSavingDefault();
            // Preserve the SDK's ComWrappers, dispatcher context and XAML startup.
            XamlGeneratedProgram.XamlGeneratedMain();
        }
        finally { ProcessComSecurity.Shutdown(); }
    }
}
