using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace YogaITS.Tray.UI;

internal static class WindowHelpers
{
    public static void Configure(Window window, int width, int height)
    {
        window.AppWindow.Resize(new SizeInt32(width, height));

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "YogaITS.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }
}
