using Microsoft.UI.Xaml;
using YogaITS.Tray.Services;

namespace YogaITS.Tray;

public partial class App : Application
{
    private static Mutex? _mutex;
    private MainWindow? _window;
    private EventWaitHandle? _openWindowSignal;
    private RegisteredWaitHandle? _openWindowWait;
    private EventWaitHandle? _openMiniWindowSignal;
    private RegisteredWaitHandle? _openMiniWindowWait;

    public App()
    {
        UnhandledException += (_, args) => System.Diagnostics.Trace.WriteLine(args.Exception);
        if (Environment.GetCommandLineArgs().Contains("--check-ui-contract"))
            UnhandledException += (_, args) => Console.Error.WriteLine(args.Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--check-ui-contract"))
        {
            CheckUiContract();
            return;
        }
#if LIQUID_GLASS
        BundledRuntime.CheckLoadedModules();
        UI.GlassSettingsController.LoadSavedOptions();
#endif
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool openControlCenter = AppLaunchPolicy.ShouldOpenControlCenter(arguments);
        bool openMiniWindow = AppLaunchPolicy.ShouldOpenMiniWindow(arguments);
        // Create before checking the mutex so a request arriving during primary
        // initialization remains signaled until its UI host is ready.
        _openWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\YogaITS.Tray.OpenControlCenter");
        _openMiniWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\YogaITS.Tray.OpenMiniWindow");
        _mutex = new Mutex(true, @"Local\YogaITS.Tray.Singleton", out bool createdNew);

        if (!createdNew)
        {
            if (openControlCenter) _openWindowSignal.Set();
            if (openMiniWindow) _openMiniWindowSignal.Set();
            Environment.Exit(0);
            return;
        }

        // MainWindow is only a hidden HWND/message host for the tray icon.
        // Do NOT Activate() it here; activating and then hiding it causes a
        // brief blank/black top-level window flash during startup.
        _window = new MainWindow();
        _openWindowWait = ThreadPool.RegisterWaitForSingleObject(_openWindowSignal,
            (_, _) => _window.RequestOpenControlCenter(), null, Timeout.Infinite, executeOnlyOnce: false);
        _openMiniWindowWait = ThreadPool.RegisterWaitForSingleObject(_openMiniWindowSignal,
            (_, _) => _window.RequestOpenMiniWindow(), null, Timeout.Infinite, executeOnlyOnce: false);
        if (openControlCenter) _window.RequestOpenControlCenter();
        if (openMiniWindow) _window.RequestOpenMiniWindow();
    }

    private async void CheckUiContract()
    {
        try { await Tests.UiContractChecks.RunAsync(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 9; }
        finally { Exit(); }
    }
}
