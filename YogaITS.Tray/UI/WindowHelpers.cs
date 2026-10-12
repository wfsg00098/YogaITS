using Microsoft.UI.Xaml;
using Windows.Graphics;
using YogaITS.Tray.Services;

namespace YogaITS.Tray.UI;

internal static class WindowHelpers
{
    public static void Configure(Window window, int width, int height)
    {
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Resize(new SizeInt32(
            WindowSizing.Fit(width, dpi, area.Width), WindowSizing.Fit(height, dpi, area.Height)));

        SetAppIcon(window);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public static void SetAppIcon(Window window)
    {
        var icon = ApplicationIcon.Load(small: false);
        try
        {
            window.AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(icon.DangerousGetHandle()));
            window.Closed += (_, _) => icon.Dispose();
        }
        catch
        {
            icon.Dispose();
            throw;
        }
    }
}
