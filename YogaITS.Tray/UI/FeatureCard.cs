using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace YogaITS.Tray.UI;

// Feature panels use the same option tiles and accent treatment as the tray menu.
internal sealed class FeatureCard
{
    public Border View { get; } = new();
    public StackPanel Footer { get; } = new() { Spacing = 6 };
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private Action<StackPanel>? _render;
    private bool _busy;
    private string? _lastError;

    public FeatureCard(string title)
    {
        View.Padding = new Thickness(12);
        View.CornerRadius = new CornerRadius(8);
        View.BorderThickness = new Thickness(1);
        View.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(_body);
        content.Children.Add(_status);
        content.Children.Add(Footer);
        View.Child = content;
    }

    public void Update(bool visible, Action<StackPanel> render)
    {
        _render = render;
        if (_busy)
            return;
        View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _body.Children.Clear();
        render(_body);
        if (_lastError is not null)
            SetStatus(_lastError);
    }

    public void SetStatus(string message) => _status.Text = message;

    public Button Option(string label, bool selected, Func<Task> action, bool enabled = true)
    {
        Button button = TrayMenuWindow.CreateOptionTile(label, selected, false, 48);
        button.IsEnabled = enabled;
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    public void Options(StackPanel body, params Button[] buttons) =>
        body.Children.Add(TrayMenuWindow.CreateTileGrid(buttons, Math.Min(3, buttons.Length), 48));

    public void Note(StackPanel body, string text) => body.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
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
            _body.IsHitTestVisible = true;
            _body.Opacity = 1;
            if (_render is not null)
            {
                _body.Children.Clear();
                _render(_body);
            }
            if (_lastError is not null)
                SetStatus(_lastError);
        }
    }
}
