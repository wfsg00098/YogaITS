using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI;

// Use theme transitions, never animate layout dimensions or take over input.
internal static class SettingsMotion
{
    private static readonly UISettings Settings = new();

    public static void AttachHover(Button button)
    {
        var hover = new SolidColorBrush();
        var pressed = new SolidColorBrush();
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = pressed;
        void UpdateColors()
        {
            byte channel = button.ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)0;
            hover.Color = Color.FromArgb(10, channel, channel, channel);
            pressed.Color = Color.FromArgb(16, channel, channel, channel);
        }
        void UpdateTransition()
        {
            button.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(button) > 0 &&
                VisualTreeHelper.GetChild(button, 0) is ContentPresenter presenter)
                presenter.BackgroundTransition = new BrushTransition
                {
                    Duration = Settings.AnimationsEnabled ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero
                };
        }
        button.ActualThemeChanged += (_, _) => UpdateColors();
        button.Loaded += (_, _) =>
        {
            UpdateColors();
            UpdateTransition();
        };
        button.PointerEntered += (_, _) => UpdateTransition();
        button.PointerExited += (_, _) => UpdateTransition();
        UpdateColors();
    }

    public static void Reposition(Panel panel)
    {
        bool enabled = Settings.AnimationsEnabled;
        panel.ChildrenTransitions ??= new TransitionCollection();
        if (enabled && panel.ChildrenTransitions.Count == 0)
            panel.ChildrenTransitions.Add(new RepositionThemeTransition { IsStaggeringEnabled = false });
        else if (!enabled)
            panel.ChildrenTransitions.Clear();
    }

    public static void SeparateDivider(Border group)
    {
        // A bottom border follows resized bounds immediately; a distinct child
        // has a changing layout position that RepositionThemeTransition animates.
        var content = group.Child;
        var padding = group.Padding;
        group.Child = null;
        group.Padding = new Thickness(0);
        group.BorderThickness = new Thickness(0);
        var layout = new StackPanel();
        layout.Children.Add(new Border { Child = content, Padding = padding });
        layout.Children.Add(new Border { Style = TrayMenuWindow.GetSettingsStyle("SettingsDividerStyle") });
        Reposition(layout);
        group.Child = layout;
    }

    public static void RepositionGroup(Border group)
    {
        if (group.Child is Panel layout) Reposition(layout);
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
        // ContentThemeTransition animates the content's opacity toward 1.
        // Keep the secondary-text dimming on the stable parent, so completing
        // the transition cannot jump from opaque text back to 70% opacity.
        control.Opacity = 0.7;
        var description = TrayMenuWindow.CreateDescription(text);
        description.Opacity = 1;
        control.Content = description;
    }
}
