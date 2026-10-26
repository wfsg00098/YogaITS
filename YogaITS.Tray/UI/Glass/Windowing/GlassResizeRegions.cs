using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Windows.Graphics;
using Windows.System;
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
    private readonly Action<bool>? _resizingChanged;
    private readonly WindowResizeGesture _gesture = new();
    private ResizeGrip? _activeGrip;
    private Pointer? _pointer;
    private bool _disposed;
    private const double Grip = 4;

    public GlassResizeRegions(Window window, Grid root, Action<bool>? resizingChanged = null)
    {
        _window = window;
        _root = root;
        _resizingChanged = resizingChanged;
        root.KeyDown += KeyDown;
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
        var grip = new ResizeGrip(hit, this)
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
        bool visible = enabled && GlassWindowFrame.ResizeGrip(hwnd) > 0;
        if (!visible) FinishResize();
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pressed(ResizeGrip grip, int hit, PointerRoutedEventArgs args)
    {
        if (_disposed || Visibility != Visibility.Visible ||
            args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            !args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || !TryScreenPoint(args, out var point)) return;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (GlassWindowFrame.ResizeGrip(hwnd) == 0) return;
        FinishResize();
        if (!grip.CapturePointer(args.Pointer)) return;
        var position = _window.AppWindow.Position;
        var size = _window.AppWindow.Size;
        var minimum = GlassWindowFrame.MinimumTrackSize(hwnd);
        _gesture.Begin(hit, point.X, point.Y, (position.X, position.Y, size.Width, size.Height), minimum);
        _activeGrip = grip;
        _pointer = args.Pointer;
        args.Handled = true;
        _resizingChanged?.Invoke(true);
    }

    private void Moved(ResizeGrip grip, PointerRoutedEventArgs args)
    {
        if (_pointer?.PointerId != args.Pointer.PointerId) return;
        if (args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) MoveWindow(args);
        else FinishResize();
        args.Handled = true;
    }

    private void Released(PointerRoutedEventArgs args)
    {
        if (_pointer?.PointerId != args.Pointer.PointerId) return;
        MoveWindow(args);
        FinishResize();
        args.Handled = true;
    }

    private bool TryScreenPoint(PointerRoutedEventArgs args, out PointI point)
    {
        point = default;
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(_window), ref point)) return false;
        var position = args.GetCurrentPoint(_root).Position;
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        point.X += (int)Math.Round(position.X * scale);
        point.Y += (int)Math.Round(position.Y * scale);
        return true;
    }

    private void MoveWindow(PointerRoutedEventArgs args)
    {
        if (!TryScreenPoint(args, out var point) || !_gesture.TryMove(point.X, point.Y, out var bounds)) return;
        var current = _window.AppWindow;
        if (current.Position.X != bounds.X || current.Position.Y != bounds.Y ||
            current.Size.Width != bounds.Width || current.Size.Height != bounds.Height)
            current.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private void FinishResize()
    {
        if (!_gesture.Active) return;
        var grip = _activeGrip;
        var pointer = _pointer;
        _activeGrip = null;
        _pointer = null;
        _gesture.End();
        if (pointer is not null) grip?.ReleasePointerCapture(pointer);
        _resizingChanged?.Invoke(false);
    }

    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_gesture.Active || args.Key != VirtualKey.Escape) return;
        var original = _gesture.Original;
        FinishResize();
        _window.AppWindow.MoveAndResize(new RectInt32(original.X, original.Y, original.Width, original.Height));
        args.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FinishResize();
        _root.KeyDown -= KeyDown;
        foreach (var grip in Children.OfType<ResizeGrip>()) grip.Dispose();
        _root.Children.Remove(this);
    }

    private sealed class ResizeGrip : Grid, IDisposable
    {
        private InputSystemCursor? _cursor;
        public ResizeGrip(int hit, GlassResizeRegions owner)
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
            PointerPressed += (_, args) => owner.Pressed(this, hit, args);
            PointerMoved += (_, args) => owner.Moved(this, args);
            PointerReleased += (_, args) => owner.Released(args);
            PointerCanceled += (_, args) => { if (owner._pointer?.PointerId == args.Pointer.PointerId) owner.FinishResize(); };
            PointerCaptureLost += (_, args) => { if (owner._pointer?.PointerId == args.Pointer.PointerId) owner.FinishResize(); };
        }
        public void Dispose()
        {
            if (_cursor is null) return;
            if (IsLoaded) ProtectedCursor = null;
            _cursor?.Dispose();
            _cursor = null;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref PointI point);
}
