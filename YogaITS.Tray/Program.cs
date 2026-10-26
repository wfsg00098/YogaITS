#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Runtime;
#endif
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color.LenovoTcon;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YogaITS.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Report provenance before single-instance forwarding or hardware/UI startup.
        if (args.Contains("--version"))
        {
            Console.WriteLine($"YogaITS {Services.Updates.UpdatePolicy.CurrentIdentity}");
            Console.WriteLine($"Build: {AppBuild.Flavor}");
            Console.WriteLine($"Original source: https://github.com/{Services.Updates.UpdatePolicy.Repository}");
            Console.WriteLine("License: BSD-3-Clause; see LICENSE and NOTICE.");
            return;
        }
#if UI_CHECKS
        bool check = args.Contains("--check-runtime") || args.Contains("--check-ui-contract");
#else
        if (args.Contains("--check-ui-contract"))
        {
            Console.Error.WriteLine("Use Tests/UiContractChecks.ps1 to build and run the isolated UI check host.");
            Environment.ExitCode = 2;
            return;
        }
        bool check = args.Contains("--check-runtime");
#endif
        if (!check && AppSession.TryForwardExistingInstance(args)) return;
        // A reset helper may still own the display stack; do not create WinUI resources yet.
        try { if (!check) GpuDeviceRestartProtocol.WaitForIdle(TimeSpan.FromSeconds(45)); }
        catch (Exception ex) { GpuDeviceRestartProtocol.ShowError(ex.Message); return; }
#if LIQUID_GLASS
        if (!check && !CaptureIdentityBootstrap.ContinueStartup(args)) return;
#endif
        // Process security must precede UI/OLE/COM proxies.
        ProcessComSecurity.InitializeEarly();
        try
        {
#if LIQUID_GLASS
            BundledRuntime.Initialize();
            if (check) Console.WriteLine("UI check: bundled runtime initialized");
            if (args.Contains("--check-runtime")) { BundledRuntime.WriteReport(); return; }
#else
            if (args.Contains("--check-runtime"))
            {
                Console.WriteLine("BuildFlavor=native; system WinUI runtime; no glass or capture compiled.");
                return;
            }
#endif
            // Keep the UI on its existing/power-saving GPU and the dedicated
            // keep-awake executable on the high-performance GPU.
            // Windows can still require a subsequent launch to apply a new preference.
            if (!check) AppGpuPreference.TrySetPowerSavingDefault();
            // Preserve the SDK's ComWrappers, dispatcher context and XAML startup.
            StartXaml();
        }
        finally { ProcessComSecurity.Shutdown(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartXaml()
    {
        _ = SetCurrentProcessExplicitAppUserModelID(AppIdentity.AppUserModelId);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            try { new App(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
