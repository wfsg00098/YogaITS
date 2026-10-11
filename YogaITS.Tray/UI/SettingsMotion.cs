using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI;

// Use theme transitions, never animate layout dimensions or take over input.
internal static class SettingsMotion
{
    private static readonly UISettings Settings = new();

    public static void Reposition(Panel panel)
    {
        bool enabled = Settings.AnimationsEnabled;
        panel.ChildrenTransitions ??= new TransitionCollection();
        if (enabled && panel.ChildrenTransitions.Count == 0)
            panel.ChildrenTransitions.Add(new RepositionThemeTransition { IsStaggeringEnabled = false });
        else if (!enabled)
            panel.ChildrenTransitions.Clear();
    }

    public static void SetDescription(ContentControl control, string text)
    {
        if (control.Content is TextBlock current && current.Text == text) return;
        control.ContentTransitions ??= new TransitionCollection();
        control.ContentTransitions.Clear();
        if (control.Content is not null && Settings.AnimationsEnabled)
            control.ContentTransitions.Add(new ContentThemeTransition
            {
                HorizontalOffset = 0, VerticalOffset = 0
            });
        control.Content = TrayMenuWindow.CreateDescription(text);
    }
}
