using YogaITS.Tray.UI.Controls;
using YogaITS.Tray.UI.Presentation;
using YogaITS.Tray.UI.Shell;
using YogaITS.Tray.UI.Glass.Backgrounds;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Settings;
using YogaITS.Tray.UI.Glass.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace YogaITS.Tray.UI.Glass.Controls;

internal sealed class UiOptionsPanel : IDisposable
{
    private readonly TrayMenuWindow _window;
    private readonly FeatureCard _card = new("UI选项");
    private readonly HardwareChoiceGroup _style, _source;
    private readonly AnimatedOptionHost _imageHost;
    private readonly Button _imageButton;
    private bool _connected, _disposed, _picking;
    private string? _error;
    public Border View => _card.View;

    public UiOptionsPanel(TrayMenuWindow window)
    {
        _window = window;
        _style = new(window, "UI选项", Refresh, error => ReportError("UI切换失败：", error));
        _source = new(window, "UI选项中的背景来源", Refresh, error => ReportError("背景切换失败：", error));
        _imageButton = TrayMenuWindow.CreateOptionTile("选择图像", false, true,
            TrayMenuWindow.OptionTileHeightDip, TrayMenuWindow.ControlCenterButtonFontSize);
        _imageButton.Click += async (_, _) => await PickImageAsync();
        var imageGrid = TrayMenuWindow.CreateTileGrid([_imageButton], 1, TrayMenuWindow.OptionTileHeightDip);
        _imageHost = new(() => LiquidGlassWindow.EffectsEnabled(window), p => WindowMaterial.SetReveal(imageGrid, p));
        _imageHost.SetChild(imageGrid);
        View.Loaded += Loaded;
        View.Unloaded += Unloaded;
        Refresh();
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_connected || _disposed) return;
        _connected = true;
        LiquidGlassWindow.PreferenceChanged += Refresh;
        LiquidGlassWindow.AvailabilityChanged += Refresh;
        LiquidGlassWindow.BackgroundStatusChanged += Refresh;
        Refresh();
    }

    private void Unloaded(object sender, RoutedEventArgs args) => Disconnect();
    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        LiquidGlassWindow.PreferenceChanged -= Refresh;
        LiquidGlassWindow.AvailabilityChanged -= Refresh;
        LiquidGlassWindow.BackgroundStatusChanged -= Refresh;
    }

    private void ReportError(string prefix, Exception error)
    {
        _error = prefix + error.Message;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        bool glass = !LiquidGlassWindow.NativeRequested;
        _style.Update([
            new("native", "WinUI3", () => SelectStyleAsync(true)),
            new("glass", "液态玻璃", () => SelectStyleAsync(false), LiquidGlassWindow.Available(_window))
        ], glass ? "glass" : "native", true, 2);
        _source.Update(GlassBackgroundOptions.Choices.Select(choice => new HardwareChoice(
            choice.Source.ToString(), choice.Label, () => SelectSourceAsync(choice.Source),
            choice.Source != GlassBackgroundSource.TransparentDesktop || GlassCapability.CaptureSupported)),
            GlassBackgroundOptions.Source.ToString(), true, GlassBackgroundOptions.Choices.Count, visible: glass);
        bool image = glass && GlassBackgroundOptions.Source == GlassBackgroundSource.StaticImage;
        _imageHost.SetExpanded(image);
        _card.Update(true, body =>
        {
            _card.Content(body, _style.View);
            _card.Note(body, "背景来源", visible: glass);
            _card.Content(body, _source.View);
            _card.Content(body, _imageHost);
        });
        string status = image ? GlassBackgroundOptions.ImagePath is { } path ? Path.GetFileName(path) : "请选择一张图像。" : "";
        if (glass && LiquidGlassWindow.ImageError(_window) is { } error) status = error;
        _card.SetStatus(_error ?? status);
    }

    private async Task SelectStyleAsync(bool native)
    {
        _error = null;
        await GlassSettingsController.SelectStyleAsync(native);
    }

    private async Task SelectSourceAsync(GlassBackgroundSource source)
    {
        _error = null;
        await GlassSettingsController.SelectSourceAsync(source);
    }

    private async Task PickImageAsync()
    {
        if (_disposed || _picking) return;
        _picking = true;
        _error = null;
        _imageButton.IsEnabled = false;
        try
        {
            // The Windows SDK picker cannot run in our elevated process.
            var picker = new FileOpenPicker(_window.AppWindow.Id);
            foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !_disposed && GlassBackgroundOptions.Source == GlassBackgroundSource.StaticImage)
                await GlassSettingsController.SelectImageAsync(file.Path);
        }
        catch (Exception ex) { if (!_disposed) _error = "无法选择图像：" + ex.Message; }
        finally { _picking = false; if (!_disposed) { _imageButton.IsEnabled = true; Refresh(); } }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        View.Loaded -= Loaded;
        View.Unloaded -= Unloaded;
        _style.Dispose();
        _source.Dispose();
        _imageHost.Dispose();
        _card.Dispose();
    }
}
