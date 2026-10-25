using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YogaITS.Tray.UI;

internal static class WindowMaterial
{
    public static bool EffectsEnabled(Window window)
    {
#if LIQUID_GLASS
        return LiquidGlassWindow.EffectsEnabled(window);
#else
        return false;
#endif
    }

    public static void Attach(Window window, Grid root)
    {
#if LIQUID_GLASS
        LiquidGlassWindow.Attach(window, root);
#else
        window.SystemBackdrop = new MicaBackdrop();
#endif
    }

    public static void AttachTile(Button button, Grid content, bool selected)
    {
#if LIQUID_GLASS
        LiquidGlassWindow.AttachTile(button, content, selected);
#endif
    }

    public static void SetReveal(UIElement element,double progress)
    {
#if LIQUID_GLASS
        LiquidGlassWindow.SetReveal(element,progress);
#else
        element.Opacity=Math.Clamp(progress,0,1);
#endif
    }

    public static void SetTileState(Button button, bool emphasized)
    {
#if LIQUID_GLASS
        LiquidGlassWindow.SetTileState(button, emphasized);
#endif
    }

    public static void RefreshTileAppearance(Button button)
    {
#if LIQUID_GLASS
        LiquidGlassWindow.RefreshTileAppearance(button);
#endif
    }
}
