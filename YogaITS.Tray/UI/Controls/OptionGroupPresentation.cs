#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Windowing;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YogaITS.Tray.UI.Controls;

// Two views of one confirmed setting; switching presentation never requests a setting.
internal sealed class OptionGroupPresentation : Grid, IDisposable
{
    private readonly UIElement? _glass;
    private UIElement _native;
    private readonly Func<bool> _useGlass;
    private readonly Action? _cancelPreview;
    private bool? _showingGlass;
    private bool _connected, _disposed;
    public event Action? PresentationChanged;
    public OptionGroupPresentation(UIElement? glass, UIElement native, Func<bool> useGlass, Action? cancelPreview = null)
    {
        _glass = glass; _native = native; _useGlass = useGlass; _cancelPreview = cancelPreview;
        Children.Add(native);
        if (glass is not null) Children.Add(glass);
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
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
        bool glass = _useGlass();
        if (_showingGlass == glass) return;
        if (!glass && _showingGlass == true) _cancelPreview?.Invoke();
        _showingGlass = glass;
        if (_glass is not null) _glass.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        _native.Visibility = glass ? Visibility.Collapsed : Visibility.Visible;
        PresentationChanged?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        if (_glass is IDisposable owned) owned.Dispose();
    }
}
