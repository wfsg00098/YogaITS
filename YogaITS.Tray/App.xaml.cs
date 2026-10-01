using Microsoft.UI.Xaml;

namespace YogaITS.Tray;

public partial class App : Application
{
    private static Mutex? _mutex;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(true, @"Local\YogaITS.Tray.Singleton", out bool createdNew);

        if (!createdNew)
        {
            Environment.Exit(0);
            return;
        }

        // MainWindow is only a hidden HWND/message host for the tray icon.
        // Do NOT Activate() it here; activating and then hiding it causes a
        // brief blank/black top-level window flash during startup.
        _window = new MainWindow();
    }
}
