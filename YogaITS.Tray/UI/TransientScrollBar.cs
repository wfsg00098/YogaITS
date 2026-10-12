using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI;

// Only changes the native scrollbar's presentation. ScrollViewer still owns
// touch gestures, inertia, wheel/keyboard input, and thumb dragging.
internal sealed class TransientScrollBar : IDisposable
{
    private readonly ScrollViewer _scroll;
    private readonly ScrollBar _bar;
    private readonly DispatcherTimer _hideTimer = new();
    private readonly PointerEventHandler _pressed;
    private readonly PointerEventHandler _released;
    private readonly UISettings _uiSettings = new();
    private readonly VisualStateGroup? _indicatorStates;
    private readonly List<VisualTransition> _fadeInTransitions = new();
    private readonly long _indicatorModeCallback;
    private bool _visible;
    private bool _scrolling;
    private bool _hovering;
    private bool _dragging;
    private bool _disposed;
    private double _lastOffset;

    public TransientScrollBar(ScrollViewer scroll, ScrollBar bar)
    {
        _scroll = scroll;
        _bar = bar;
        _lastOffset = scroll.VerticalOffset;
        _pressed = Bar_PointerPressed;
        _released = Bar_PointerReleased;
        _bar.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(_bar) > 0 &&
            VisualTreeHelper.GetChild(_bar, 0) is FrameworkElement root)
        {
            _indicatorStates = VisualStateManager.GetVisualStateGroups(root)
                .FirstOrDefault(group => group.Name == "ScrollingIndicatorStates");
            if (_indicatorStates != null)
            {
                // Reuse the native fade-out storyboard. Add generated fade-ins
                // to its existing states, using the template's opacity duration.
                foreach (string target in new[] { "TouchIndicator", "MouseIndicator" })
                {
                    var transition = new VisualTransition
                    {
                        From = "NoIndicator",
                        To = target,
                        GeneratedDuration = new Duration(TimeSpan.FromMilliseconds(83))
                    };
                    _indicatorStates.Transitions.Add(transition);
                    _fadeInTransitions.Add(transition);
                }
            }
        }
        _indicatorModeCallback = _bar.RegisterPropertyChangedCallback(
            ScrollBar.IndicatorModeProperty, (_, _) =>
            {
                // ScrollViewer also changes indicator mode on pointer movement
                // and its own auto-hide timer. Reconcile after that native update
                // without changing input handling or restarting our hide timer.
                _bar.DispatcherQueue.TryEnqueue(ApplyIndicatorState);
            });
        VisualStateManager.GoToState(_bar, "NoIndicator", false);
        _indicatorStates?.CurrentState?.Storyboard?.SkipToFill();
        _hideTimer.Tick += HideTimer_Tick;
        _scroll.ViewChanging += Scroll_ViewChanging;
        _scroll.ViewChanged += Scroll_ViewChanged;
        _bar.PointerEntered += Bar_PointerEntered;
        _bar.PointerExited += Bar_PointerExited;
        // Native Thumb handles these events. Observe them without taking capture
        // or marking them handled, including capture lost on release/cancellation.
        _bar.AddHandler(UIElement.PointerPressedEvent, _pressed, true);
        _bar.AddHandler(UIElement.PointerReleasedEvent, _released, true);
        _bar.AddHandler(UIElement.PointerCanceledEvent, _released, true);
        _bar.AddHandler(UIElement.PointerCaptureLostEvent, _released, true);
        Show();
        HideAfter(1000);
    }

    private void Show()
    {
        if (_disposed) return;
        _hideTimer.Stop();
        _visible = _scroll.ScrollableHeight > 0;
        ApplyIndicatorState();
    }

    private void ApplyIndicatorState()
    {
        if (_disposed) return;
        bool animate = _uiSettings.AnimationsEnabled;
        if (!_uiSettings.AutoHideScrollBars) _visible = _scroll.ScrollableHeight > 0;
        string state = !_visible ? "NoIndicator" :
            !_uiSettings.AutoHideScrollBars || _hovering || _dragging ? "MouseIndicator" : "TouchIndicator";
        VisualStateManager.GoToState(_bar, state, animate);
        // useTransitions=false skips generated transitions, not the native
        // state's storyboard. Complete that too when system animations are off.
        if (!animate) _indicatorStates?.CurrentState?.Storyboard?.SkipToFill();
    }

    private void HideAfter(int milliseconds = 180)
    {
        if (_disposed) return;
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        if (!_uiSettings.AutoHideScrollBars) return;
        _hideTimer.Start();
    }

    private void HideTimer_Tick(object? sender, object args)
    {
        _hideTimer.Stop();
        if (_disposed || !_uiSettings.AutoHideScrollBars || _scrolling || _hovering || _dragging) return;
        _visible = false;
        ApplyIndicatorState();
    }

    private void Scroll_ViewChanging(object? sender, ScrollViewerViewChangingEventArgs args)
    {
        if (args.NextView.VerticalOffset == _lastOffset) return;
        _scrolling = true;
        Show();
    }

    private void Scroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        bool moved = _scroll.VerticalOffset != _lastOffset;
        _lastOffset = _scroll.VerticalOffset;
        // Capability/layout refreshes also raise ViewChanged. They must not
        // shorten the opening hint or flash the bar when content has not moved.
        if (!moved && !_scrolling)
        {
            if (!_uiSettings.AutoHideScrollBars) Show();
            return;
        }
        _scrolling = args.IsIntermediate;
        Show();
        if (!_scrolling) HideAfter();
    }

    private void Bar_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _hovering = true;
        Show();
    }

    private void Bar_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _hovering = false;
        if (!_scrolling && !_dragging) HideAfter();
    }

    private void Bar_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _dragging = true;
        Show();
    }

    private void Bar_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _dragging = false;
        if (!_scrolling) HideAfter();
    }

    public void RefreshPreferences()
    {
        if (_disposed) return;
        Show();
        if (_uiSettings.AutoHideScrollBars) HideAfter();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hideTimer.Stop();
        _hideTimer.Tick -= HideTimer_Tick;
        _bar.UnregisterPropertyChangedCallback(ScrollBar.IndicatorModeProperty, _indicatorModeCallback);
        if (_indicatorStates != null)
            foreach (var transition in _fadeInTransitions)
                _indicatorStates.Transitions.Remove(transition);
        _scroll.ViewChanging -= Scroll_ViewChanging;
        _scroll.ViewChanged -= Scroll_ViewChanged;
        _bar.PointerEntered -= Bar_PointerEntered;
        _bar.PointerExited -= Bar_PointerExited;
        _bar.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
        _bar.RemoveHandler(UIElement.PointerReleasedEvent, _released);
        _bar.RemoveHandler(UIElement.PointerCanceledEvent, _released);
        _bar.RemoveHandler(UIElement.PointerCaptureLostEvent, _released);
    }
}
