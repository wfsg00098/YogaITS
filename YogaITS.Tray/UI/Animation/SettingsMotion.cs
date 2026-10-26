using YogaITS.Tray.UI.Accessibility;
using YogaITS.Tray.UI.Presentation;
using YogaITS.Tray.UI.Shell;
#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Animation;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI.Animation;

// Glass reflow shares the physical integrator; native UI keeps theme transitions.
internal static class SettingsMotion
{
    private static readonly UISettings Settings = new();
    internal static bool AnimationsEnabled => Settings.AnimationsEnabled;

    public static void AttachHover(Button button)
    {
        var hover = new SolidColorBrush();
        var pressed = new SolidColorBrush();
        var hoverText = new SolidColorBrush();
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = pressed;
        button.Resources["ButtonForegroundPointerOver"] = hoverText;
        button.Resources["ButtonForegroundPressed"] = hoverText;
        void UpdateColors()
        {
            RefreshHoverColors(button);
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

    public static void RefreshHoverColors(Button button)
    {
        byte channel = button.ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)0;
#if LIQUID_GLASS
        // Glass cards render input feedback above their material, not behind it.
        ((SolidColorBrush)button.Resources["ButtonBackgroundPointerOver"]).Color = Microsoft.UI.Colors.Transparent;
        ((SolidColorBrush)button.Resources["ButtonBackgroundPressed"]).Color = Microsoft.UI.Colors.Transparent;
        WindowMaterial.RefreshTileAppearance(button);
#else
        ((SolidColorBrush)button.Resources["ButtonBackgroundPointerOver"]).Color =
            UiAccessibility.HighContrast ? UiAccessibility.SystemColor(13) : Color.FromArgb(10, channel, channel, channel);
        ((SolidColorBrush)button.Resources["ButtonBackgroundPressed"]).Color =
            UiAccessibility.HighContrast ? UiAccessibility.SystemColor(13) : Color.FromArgb(16, channel, channel, channel);
#endif
        ((SolidColorBrush)button.Resources["ButtonForegroundPointerOver"]).Color =
            UiAccessibility.HighContrast ? UiAccessibility.SystemColor(14) : Color.FromArgb(255, channel, channel, channel);
    }

    public static void Reposition(Panel panel, bool animateNative = true)
    {
#if LIQUID_GLASS
        AccelerationLayoutMotion.Attach(panel, nativeTransitions: animateNative);
#else
        panel.ChildrenTransitions ??= new TransitionCollection();
        if (animateNative && Settings.AnimationsEnabled && !panel.ChildrenTransitions.OfType<RepositionThemeTransition>().Any())
            panel.ChildrenTransitions.Add(new RepositionThemeTransition { IsStaggeringEnabled = false });
        if (!Settings.AnimationsEnabled) panel.ChildrenTransitions.Clear();
#endif
    }

    public static void RevealBefore(UIElement appearing, UIElement following)
    {
        // Only existing content moves. Newly visible content fades at its final
        // position instead of repositioning from its previously unarranged origin.
        appearing.Transitions ??= new TransitionCollection();
        following.Transitions ??= new TransitionCollection();
        if (!Settings.AnimationsEnabled)
        {
            appearing.Transitions.Clear();
            following.Transitions.Clear();
            return;
        }
        if (appearing.Transitions.Count == 0)
            appearing.Transitions.Add(new EntranceThemeTransition
            {
                FromHorizontalOffset = 0, FromVerticalOffset = 0, IsStaggeringEnabled = false
            });
        if (following.Transitions.Count == 0)
            following.Transitions.Add(new RepositionThemeTransition { IsStaggeringEnabled = false });
    }

    public static void SeparateDivider(Border group)
    {
        // A bottom border follows resized bounds immediately; a distinct child
        // has a changing layout position that the active motion model animates.
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
        // Fade only element opacity. Secondary text uses the theme brush's
        // color/alpha, so completing a fade cannot change its final contrast.
        control.Opacity = 1;
        var description = TrayMenuWindow.CreateDescription(text);
        description.Opacity = 1;
        control.Content = description;
    }
}
