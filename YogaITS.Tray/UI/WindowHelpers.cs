using Microsoft.UI.Xaml;
using Windows.Graphics;
using YogaITS.Tray.Services;

namespace YogaITS.Tray.UI;

internal static class WindowHelpers
{
    public static void Configure(Window window, int width, int height)
    {
        window.AppWindow.Resize(new SizeInt32(width, height));

        SetAppIcon(window);
    }

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
