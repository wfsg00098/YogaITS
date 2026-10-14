using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color.LenovoTcon;

namespace YogaITS.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Match the final probe: process security precedes UI/OLE/COM proxies.
        ProcessComSecurity.InitializeEarly();
        try
        {
            // Register only this executable's default before WinUI creates graphics resources.
            // Windows can still require a subsequent launch to apply a new preference.
            AppGpuPreference.TrySetPowerSavingDefault();
            // Preserve the SDK's ComWrappers, dispatcher context and XAML startup.
            XamlGeneratedProgram.XamlGeneratedMain();
        }
        finally { ProcessComSecurity.Shutdown(); }
    }
}
