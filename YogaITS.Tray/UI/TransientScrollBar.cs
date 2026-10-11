using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

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
        if (_scroll.ScrollableHeight <= 0) { _bar.Opacity = 0; return; }
        _bar.Opacity = 1;
        VisualStateManager.GoToState(_scroll, _hovering || _dragging ? "MouseIndicator" : "TouchIndicator", false);
    }

    private void HideAfter(int milliseconds = 180)
    {
        if (_disposed) return;
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _hideTimer.Start();
    }

    private void HideTimer_Tick(object? sender, object args)
    {
        _hideTimer.Stop();
        if (_disposed || _scrolling || _hovering || _dragging) return;
        // Do not collapse or disable the bar: keep geometry, accessibility and
        // pointer discovery intact, independently of native auto-hide delays.
        _bar.Opacity = 0;
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
        if (!moved && !_scrolling) return;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hideTimer.Stop();
        _hideTimer.Tick -= HideTimer_Tick;
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
