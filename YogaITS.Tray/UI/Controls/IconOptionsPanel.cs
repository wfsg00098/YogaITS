using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using YogaITS.Tray.Services.QuickSettings;
using YogaITS.Tray.UI.Presentation;

namespace YogaITS.Tray.UI.Controls;

internal sealed class IconOptionsPanel : IDisposable
{
    private const string Title = "图标选项";
    private static readonly string[] Labels = ["任务栏托盘", "快速设置"];
    private readonly FeatureCard _card = new(Title);
    private readonly ChoiceBar _bar;
    private readonly OptionGroupPresentation _presentation;
    private readonly ToggleButton[] _segments;
    private readonly Action<IconPlacement> _select;
    private IconPlacement _placement;
    private bool _ready;
    private string? _error;

    public Border View => _card.View;

    public IconOptionsPanel(Window window, Action<IconPlacement> select)
    {
        _select = select;
        var track = new Grid
        {
            Padding = new Thickness(3),
            ColumnSpacing = 3,
            Margin = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(24)
        };
        if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out var background))
            track.Background = background as Brush;
        _segments = new ToggleButton[Labels.Length];
        for (int i = 0; i < Labels.Length; i++)
        {
            int index = i;
            track.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var button = new ToggleButton
            {
                Content = new TextBlock { Text = Labels[i], TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 42,
                CornerRadius = new CornerRadius(24)
            };
            AutomationProperties.SetName(button, Title + "中的" + Labels[i]);
            button.Click += (_, _) => Select(index);
            Grid.SetColumn(button, i);
            track.Children.Add(button);
            _segments[i] = button;
        }
        _bar = new(window, Labels, Title);
        _bar.SelectionRequested += Select;
        _presentation = new(_bar.View, track, () => WindowMaterial.EffectsEnabled(window),
            _bar.CancelPreview, _bar.RefreshPresentation);
        _card.Update(true, body => _card.Content(body, _presentation));
        Refresh(IconPlacement.Tray, false, true);
    }

    public void Refresh(IconPlacement placement, bool ready, bool available)
    {
        _placement = placement;
        _ready = ready;
        _bar.SelectIndex((int)placement, animate: true);
        _bar.SetOptionEnabled(1, available);
        _segments[0].IsChecked = placement == IconPlacement.Tray;
        _segments[1].IsChecked = placement == IconPlacement.QuickSettings;
        _segments[1].IsEnabled = available;
        OptionLabelAppearance.Decorate((TextBlock)_segments[1].Content, available);
        _card.SetStatus(_error ?? (placement == IconPlacement.QuickSettings && !ready
            ? available ? "快速设置尚未就绪。按 Win+A 打开快速设置，连接前可使用托盘。"
                        : "当前无法使用快速设置，图标显示在托盘。"
            : ""));
    }

    private void Select(int index)
    {
        _error = null;
        try { _select((IconPlacement)index); }
        catch (Exception ex)
        {
            _error = "无法切换图标位置：" + ex.Message;
            Refresh(_placement, _ready, _segments[1].IsEnabled);
        }
    }

    public void Dispose()
    {
        _presentation.Dispose();
        _bar.Dispose();
        _card.Dispose();
    }
}
