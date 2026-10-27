#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Windowing;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.UI.Layout;

namespace YogaITS.Tray.UI.Controls;

// Two views of one confirmed setting; switching presentation never requests a setting.
internal sealed class OptionGroupPresentation : Grid, IDisposable
{
    private UIElement? _glass;
    private UIElement _native;
    private readonly Func<bool> _useGlass;
    private readonly Action? _cancelPreview;
    private readonly Action? _refreshRenderer;
    private bool? _showingGlass;
    private bool _connected, _disposed;
    public event Action? PresentationChanged;
    public OptionGroupPresentation(UIElement? glass, UIElement native, Func<bool> useGlass,
        Action? cancelPreview = null, Action? refreshRenderer = null)
    {
        _glass = glass; _native = native; _useGlass = useGlass; _cancelPreview = cancelPreview;
        _refreshRenderer = refreshRenderer;
        Children.Add(native);
        if (glass is not null) Children.Add(glass);
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
        Refresh();
    }
    public void ReplaceGlass(UIElement? glass)
    {
        if (_disposed || ReferenceEquals(_glass, glass)) return;
        if (_glass is { } previous)
        {
            Children.Remove(previous);
            if (previous is IDisposable owned) owned.Dispose();
        }
        _glass = glass;
        if (glass is not null) Children.Add(glass);
        _showingGlass = null;
        Refresh();
    }
    public void ReplaceNative(UIElement native)
    {
        if (_disposed) return;
        Children.Remove(_native);
        _native = native;
        native.Visibility = _showingGlass == true ? Visibility.Collapsed : Visibility.Visible;
        Children.Insert(0, native);
    }
    private void Connect()
    {
        if (_connected || _disposed) return;
        _connected = true;
#if LIQUID_GLASS
        LiquidGlassWindow.PreferenceChanged += Refresh;
        LiquidGlassWindow.AvailabilityChanged += Refresh;
#endif
        Refresh();
    }
    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
#if LIQUID_GLASS
        LiquidGlassWindow.PreferenceChanged -= Refresh;
        LiquidGlassWindow.AvailabilityChanged -= Refresh;
#endif
    }
    public void Refresh()
    {
        if (_disposed) return;
        bool glass = _glass is not null && _useGlass();
        if (_showingGlass == glass)
        {
            if (glass) _refreshRenderer?.Invoke();
            return;
        }
        if (!glass && _showingGlass == true) _cancelPreview?.Invoke();
        _showingGlass = glass;
        if (_glass is not null) _glass.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        _native.Visibility = glass ? Visibility.Collapsed : Visibility.Visible;
        if (glass) _refreshRenderer?.Invoke();
        PresentationChanged?.Invoke();
    }

    internal static void RefreshTree(UIElement root)
    {
        foreach (var presentation in WarmUiTree.Elements(root).OfType<OptionGroupPresentation>())
            presentation.Refresh();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        if (_glass is IDisposable owned) owned.Dispose();
    }
}
