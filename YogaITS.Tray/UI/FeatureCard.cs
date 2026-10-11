using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YogaITS.Tray.UI;

// Feature panels use the same option tiles and accent treatment as the tray menu.
internal sealed class FeatureCard : IDisposable
{
    public Border View { get; } = new();
    public StackPanel Footer { get; } = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, Visibility = Visibility.Collapsed
    };
    private Action<StackPanel>? _render;
    private bool _busy;
    private bool _visible;
    private bool _closed;
    private string? _lastError;

    public FeatureCard(string title)
    {
        View.Style = TrayMenuWindow.GetSettingsStyle("SettingsCardStyle");
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(TrayMenuWindow.CreateGroupTitle(title));
        content.Children.Add(_body);
        content.Children.Add(_status);
        content.Children.Add(Footer);
        View.Child = content;
    }

    public void Update(bool visible, Action<StackPanel> render)
    {
        if (_closed) return;
        _render = render;
        _visible = visible;
        if (_busy)
            return;
        View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _body.Children.Clear();
        render(_body);
        RefreshFooterVisibility();
        if (_lastError is not null)
            SetStatus(_lastError);
    }

    public void SetStatus(string message)
    {
        if (_closed) return;
        _status.Text = message;
        _status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RefreshFooterVisibility()
    {
        if (!_closed)
            Footer.Visibility = Footer.Children.Any(child => child.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        _closed = true;
        _render = null;
    }

    public Button Option(string label, bool selected, Func<Task> action, bool enabled = true)
    {
        Button button = TrayMenuWindow.CreateOptionTile(label, selected, false, TrayMenuWindow.OptionTileHeightDip,
            TrayMenuWindow.ControlCenterButtonFontSize);
        button.IsEnabled = enabled;
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    public void Options(StackPanel body, params Button[] buttons) =>
        body.Children.Add(TrayMenuWindow.CreateTileGrid(buttons, Math.Min(3, buttons.Length), TrayMenuWindow.OptionTileHeightDip));

    public void Note(StackPanel body, string text) => body.Children.Add(TrayMenuWindow.CreateDescription(text));

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed)
            return;
        _busy = true;
        _body.IsHitTestVisible = false;
        _body.Opacity = 0.65;
        _lastError = null;
        SetStatus("");
        try { await action(); }
        catch (OperationCanceledException) { _lastError = "操作已取消，状态已重新读取。"; }
        catch (Exception ex) { _lastError = ex.Message; }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                _body.IsHitTestVisible = true;
                _body.Opacity = 1;
                if (_render is not null)
                    Update(_visible, _render);
                if (_lastError is not null)
                    SetStatus(_lastError);
            }
        }
    }
}
