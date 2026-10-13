using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Color.LenovoTcon;

namespace YogaITS.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Match the final probe: process security precedes UI/OLE/COM proxies.
        ProcessComSecurity.InitializeEarly();
        var lines = new List<string>();
        ProcessComSecurity.Log(lines.Add);
        ColorDiagnosticLog.Write("Process COM security bootstrap", lines);
        try
        {
            // Preserve the SDK's ComWrappers, dispatcher context and XAML startup.
            XamlGeneratedProgram.XamlGeneratedMain();
        }
        finally { ProcessComSecurity.Shutdown(); }
    }
}
