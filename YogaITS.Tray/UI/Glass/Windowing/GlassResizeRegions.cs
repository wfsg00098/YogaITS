using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace YogaITS.Tray.UI.Glass.Windowing;

// Route the full-client frame through WinUI input too. Native hit testing alone
// is insufficient when the content island owns the pointer below the header.
internal sealed class GlassResizeRegions : Grid, IDisposable
{
    private readonly Window _window;
    private readonly Grid _root;
    private bool _disposed;
    private const double Grip = 4;

    public GlassResizeRegions(Window window, Grid root)
    {
        _window = window;
        _root = root;
        Visibility = Visibility.Collapsed;
        Margin = new(-root.Padding.Left, -root.Padding.Top, -root.Padding.Right, -root.Padding.Bottom);
        SetRowSpan(this, Math.Max(1, root.RowDefinitions.Count));
        SetColumnSpan(this, Math.Max(1, root.ColumnDefinitions.Count));
        Add(10, HorizontalAlignment.Left, VerticalAlignment.Stretch, new(0, Grip, 0, Grip));
        Add(11, HorizontalAlignment.Right, VerticalAlignment.Stretch, new(0, Grip, 0, Grip));
        Add(12, HorizontalAlignment.Stretch, VerticalAlignment.Top, new(Grip, 0, Grip, 0));
        Add(15, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, new(Grip, 0, Grip, 0));
        Add(13, HorizontalAlignment.Left, VerticalAlignment.Top, new(0));
        Add(14, HorizontalAlignment.Right, VerticalAlignment.Top, new(0));
        Add(16, HorizontalAlignment.Left, VerticalAlignment.Bottom, new(0));
        Add(17, HorizontalAlignment.Right, VerticalAlignment.Bottom, new(0));
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        root.Children.Add(this);
    }

    private void Add(int hit, HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin)
    {
        var grip = new ResizeGrip(hit, BeginResize)
        {
            HorizontalAlignment = horizontal, VerticalAlignment = vertical, Margin = margin
        };
        if (horizontal != HorizontalAlignment.Stretch) grip.Width = Grip;
        if (vertical != VerticalAlignment.Stretch) grip.Height = Grip;
        Children.Add(grip);
    }

    public void Update(bool enabled)
    {
        if (_disposed) return;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        Visibility = enabled && GlassWindowFrame.ResizeGrip(hwnd) > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BeginResize(int hit)
    {
        if (!_disposed && Visibility == Visibility.Visible)
            GlassWindowFrame.BeginResize(WinRT.Interop.WindowNative.GetWindowHandle(_window), hit);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var grip in Children.OfType<ResizeGrip>()) grip.Dispose();
        _root.Children.Remove(this);
    }

    private sealed class ResizeGrip : Grid, IDisposable
    {
        private InputSystemCursor? _cursor;
        public ResizeGrip(int hit, Action<int> beginResize)
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
            Loaded += (_, _) => ProtectedCursor = _cursor ??= InputSystemCursor.Create(GlassEdgePolicy.ResizeCursor(hit) switch
            {
                32644 => InputSystemCursorShape.SizeWestEast,
                32645 => InputSystemCursorShape.SizeNorthSouth,
                32642 => InputSystemCursorShape.SizeNorthwestSoutheast,
                _ => InputSystemCursorShape.SizeNortheastSouthwest
            });
            PointerPressed += (_, args) =>
            {
                if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
                    !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                args.Handled = true;
                beginResize(hit);
            };
        }
        public void Dispose()
        {
            if (_cursor is null) return;
            if (IsLoaded) ProtectedCursor = null;
            _cursor?.Dispose();
            _cursor = null;
        }
    }
}
