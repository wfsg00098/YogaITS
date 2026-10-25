using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YogaITS.Tray.UI;

public sealed class InfoWindow : Window
{
    private bool _configured;

    public InfoWindow(string title, string message)
    {
        Title = title;

        var root = new Grid { Padding = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = title,
            Style = TrayMenuWindow.GetSettingsStyle("SettingsInfoTitleStyle"),
            TextWrapping = TextWrapping.Wrap
        };

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var scroll = new ScrollViewer
        {
            Content = body,
            Margin = new Thickness(0, 16, 0, 16),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(scroll, 1);

        var ok = new Button
        {
            Content = "确定",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96
        };
        ok.Click += (_, _) => Close();
        Grid.SetRow(ok, 2);

        root.Children.Add(heading);
        root.Children.Add(scroll);
        root.Children.Add(ok);

        Content = root;
        WindowMaterial.Attach(this, root);
        Activated += (_, _) => ConfigureOnce(500, 280);
        root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); }
        };
    }

    private void ConfigureOnce(int width, int height)
    {
        if (_configured)
            return;

        _configured = true;
        WindowHelpers.Configure(this, width, height);
    }
}
