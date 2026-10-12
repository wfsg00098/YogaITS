using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace YogaITS.Tray.UI;

// Feature panels use the same option tiles and accent treatment as the tray menu.
internal sealed class FeatureCard : IDisposable
{
    public Border View { get; } = new();
    public StackPanel Footer { get; } = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly StackPanel _content = new() { Spacing = 8 };
    private readonly TextBlock _title;
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
    };
    private Action<StackPanel>? _render;
    private readonly TextBlock _statusDetail = new()
    {
        TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
    };
    private bool _busy;
    private bool _visible;
    private bool _closed;
    private string? _lastError;
    private int _nextChild;
    private sealed record OptionBinding(Func<Task> Action, bool KeepInteractive, bool Selected);
    private readonly ConditionalWeakTable<Button, OptionBinding> _bindings = new();

    public FeatureCard(string title)
    {
        View.Style = TrayMenuWindow.GetSettingsStyle("SettingsCardStyle");
        _status.Style = _statusDetail.Style = TrayMenuWindow.GetSettingsStyle("SettingsSecondaryTextStyle");
        SettingsMotion.Reposition(_content);
        _title = TrayMenuWindow.CreateGroupTitle(title);
        _content.Children.Add(_title);
        _content.Children.Add(_body);
        _content.Children.Add(_status);
        _content.Children.Add(_statusDetail);
        _content.Children.Add(Footer);
        View.Child = _content;
        SettingsMotion.SeparateDivider(View);
    }

    public void Update(bool visible, Action<StackPanel> render)
    {
        if (_closed) return;
        _render = render;
        _visible = visible;
        if (_busy)
            return;
        View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _nextChild = 0;
        SettingsMotion.Reposition(_content);
        SettingsMotion.Reposition(_body);
        SettingsMotion.RepositionGroup(View);
        render(_body);
        while (_body.Children.Count > _nextChild)
            _body.Children.RemoveAt(_body.Children.Count - 1);
        RefreshFooterVisibility();
        if (_lastError is not null)
            SetStatus(_lastError);
    }

    public void SetStatus(string message, string? detail = null, bool announce = false)
    {
        if (_closed) return;
        SettingsMotion.Reposition(_content);
        SettingsMotion.RepositionGroup(View);
        _status.Text = message;
        _status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        _statusDetail.Text = detail ?? "";
        _statusDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        if (announce) UiAccessibility.Announce(_title, message);
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

    public Button Option(string label, bool selected, Func<Task> action, bool enabled = true,
        bool keepInteractive = false, bool isAction = false)
    {
        Button button = TrayMenuWindow.CreateOptionTile(label, selected, isAction, TrayMenuWindow.OptionTileHeightDip,
            TrayMenuWindow.ControlCenterButtonFontSize);
        button.IsEnabled = enabled;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
            UiAccessibility.GroupedName(_title.Text, label));
        _bindings.Add(button, new OptionBinding(action, keepInteractive, selected));
        button.Click += async (_, _) =>
        {
            // Retained controls always invoke the newest snapshot's action.
            var binding = _bindings.GetValue(button, _ => throw new InvalidOperationException());
            if (!binding.KeepInteractive) { await RunAsync(binding.Action); return; }
            if (_closed) return;
            _lastError = null;
            // Long-running recording uses service state for its start/stop button.
            // It must remain renderable and clickable while awaiting completion.
            try { await binding.Action(); }
            catch (OperationCanceledException) { SetStatus("操作已取消，状态已重新读取。", announce: true); }
            catch (Exception ex) { SetStatus(ex.Message, announce: true); }
        };
        return button;
    }

    public void Options(StackPanel body, params Button[] buttons) =>
        Options(body, Math.Min(3, buttons.Length), false, buttons);

    public void Options(StackPanel body, int columns, bool stretchLastRow, params Button[] buttons)
    {
        columns = Math.Max(1, columns);
        if (_nextChild < body.Children.Count && body.Children[_nextChild] is Grid grid &&
            grid.ColumnDefinitions.Count == columns && grid.Children.Count == buttons.Length &&
            grid.Children.All(child => child is Border { Child: Button }))
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                var retained = (Button)((Border)grid.Children[i]).Child;
                var candidate = buttons[i];
                var binding = _bindings.GetValue(candidate, _ => throw new InvalidOperationException());
                _bindings.Remove(retained);
                _bindings.Add(retained, binding);
                TrayMenuWindow.UpdateOptionLabel(retained, candidate);
                TrayMenuWindow.RefreshOptionTile(retained, binding.Selected);
                retained.IsEnabled = candidate.IsEnabled;
            }
            StretchLastRow(grid, columns, stretchLastRow);
            _nextChild++;
            return;
        }
        var replacement = TrayMenuWindow.CreateTileGrid(buttons, columns, TrayMenuWindow.OptionTileHeightDip);
        StretchLastRow(replacement, columns, stretchLastRow);
        PutChild(body, replacement);
    }

    private static void StretchLastRow(Grid grid, int columns, bool stretch)
    {
        if (grid.Children.Count == 0) return;
        Grid.SetColumnSpan((FrameworkElement)grid.Children[grid.Children.Count - 1],
            stretch && grid.Children.Count % columns == 1 ? columns : 1);
    }

    public void Note(StackPanel body, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var note = _nextChild < body.Children.Count && body.Children[_nextChild] is ContentControl existing
            ? existing : new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false
            };
        SettingsMotion.SetDescription(note, text);
        if (_nextChild < body.Children.Count && ReferenceEquals(body.Children[_nextChild], note))
            _nextChild++;
        else PutChild(body, note);
    }

    private void PutChild(StackPanel body, UIElement child)
    {
        if (_nextChild < body.Children.Count)
            body.Children.RemoveAt(_nextChild);
        body.Children.Insert(_nextChild++, child);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed)
            return;
        _busy = true;
        _lastError = null;
        // Keep the current device/status row while awaiting hardware I/O.
        // Collapsing it here would shrink the group, then grow it on readback.
        try { await action(); }
        catch (OperationCanceledException) { _lastError = "操作已取消，状态已重新读取。"; }
        catch (Exception ex) { _lastError = ex.Message; }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                if (_render is not null)
                    Update(_visible, _render);
                if (_lastError is not null)
                    SetStatus(_lastError, announce: true);
                else UiAccessibility.Announce(_title, "操作完成。" + _status.Text);
            }
        }
    }
}
