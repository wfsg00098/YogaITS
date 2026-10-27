using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using YogaITS.Tray.UI.Accessibility;

namespace YogaITS.Tray.UI.Controls;

internal static class OptionLabelAppearance
{
    public static void Decorate(TextBlock label, bool enabled) => label.TextDecorations = enabled
        ? Windows.UI.Text.TextDecorations.None : Windows.UI.Text.TextDecorations.Strikethrough;

    public static Color DisabledColor(bool dark) => UiAccessibility.HighContrast
        ? UiAccessibility.SystemColor(17)
        : Color.FromArgb(85, dark ? (byte)255 : (byte)0, dark ? (byte)255 : (byte)0, dark ? (byte)255 : (byte)0);

    public static void Apply(Button button, TextBlock label, Brush? enabledForeground = null)
    {
        Decorate(label, button.IsEnabled);
        if (!button.IsEnabled)
            label.Foreground = new SolidColorBrush(DisabledColor(button.ActualTheme == ElementTheme.Dark));
        else if (enabledForeground is not null) label.Foreground = enabledForeground;
        else label.ClearValue(TextBlock.ForegroundProperty);
    }
}
