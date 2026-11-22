using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YogaITS.Tray.Services.QuickSettings;

namespace YogaITS.Tray.UI.Controls;

internal sealed class IconOptionsPanel : IDisposable
{
    private const string Title = "图标选项";
    private readonly FeatureCard _card = new(Title);
    private readonly HardwareChoiceGroup _choices;
    private readonly Action<IconPlacement> _select;
    private IconPlacement _placement;
    private bool _available = true;
    private string? _error;

    public Border View => _card.View;

    public IconOptionsPanel(Window window, Action<IconPlacement> select)
    {
        _select = select;
        _choices = new(window, Title, Render, error =>
        {
            _error = "无法切换图标位置：" + error.Message;
            UpdateStatus();
        });
        _card.Update(true, body => _card.Content(body, _choices.View));
        Render();
    }

    public void Refresh(IconPlacement placement, bool available)
    {
        _placement = placement;
        _available = available;
        Render();
    }

    private void Render()
    {
        _choices.Update([
            new(nameof(IconPlacement.Tray), "任务栏托盘", () => SelectAsync(IconPlacement.Tray)),
            new(nameof(IconPlacement.QuickSettings), "快速设置", () => SelectAsync(IconPlacement.QuickSettings), _available)
        ], _placement.ToString(), available: true, columns: 2);
        UpdateStatus();
    }

    private void UpdateStatus() => _card.SetStatus(_error ?? "");

    private Task SelectAsync(IconPlacement placement)
    {
        _error = null;
        _select(placement);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _choices.Dispose();
        _card.Dispose();
    }
}
