using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using YogaITS.Tray.Services;

namespace YogaITS.Tray.UI;

public sealed class TrayMenuWindow : Window
{
    private const int BaseWidthDip = 244;
    private readonly bool _fullWindow;
    private readonly Windows.UI.ViewManagement.UISettings _appearanceSettings = new();
    internal StackPanel AdditionalSections { get; } = new() { Spacing = 12 };

    private readonly nint _ownerHwnd;
    private readonly Func<ItsMode, Task<ItsSetResult>> _setMode;
    private readonly Func<BatteryChargeMode, Task<BatteryActionResult>> _setChargeMode;
    private readonly Func<bool, Task<BatteryToggleResult>> _setNightCharge;
    private readonly Func<bool, Task<FlipToStartResult>> _setFlipToStart;
    private readonly Action _killGpuApps;
    private readonly Action _restartGpu;
    private readonly Func<bool, Task<StartupTaskResult>> _setStartup;
    private readonly Action<string, string> _showInfo;
    private readonly Action _exitApp;

    private bool _nightChargeSupported;
    private bool _flipToStartSupported;

    private TileVisual? _nightChargeVisual;
    private TileVisual? _flipToStartVisual;
    private TileVisual? _startupVisual;

    private readonly Dictionary<ItsMode, Button> _modeTiles = [];
    private readonly Dictionary<BatteryChargeMode, Button> _chargeModeTiles = [];

    private FrameworkElement? _contentRoot;
    private bool _closed;
    private bool _shown;

    private ItsMode? _currentItsMode;
    private BatteryChargeMode? _currentChargeMode;

    private bool _nightChargeEnabled;
    private bool _flipToStartEnabled;
    private bool _startupEnabled;

    private bool _updatingItsMode;
    private bool _updatingChargeMode;
    private bool _updatingNightCharge;
    private bool _updatingFlipToStart;
    private bool _updatingStartup;

    private sealed class TileVisual
    {
        public required Button Button { get; init; }
    }

    private sealed class OptionTileState
    {
        public required TextBlock Label { get; init; }
    }

    public TrayMenuWindow(
        nint ownerHwnd,
        string modelName,
        ItsCapabilitySnapshot itsCapabilities,
        ItsModeSnapshot snapshot,
        BatteryChargeSnapshot batterySnapshot,
        BatteryToggleSnapshot nightChargeSnapshot,
        FlipToStartSnapshot flipToStartSnapshot,
        bool nvidiaGpuSupported,
        bool startupEnabled,
        Func<ItsMode, Task<ItsSetResult>> setMode,
        Func<BatteryChargeMode, Task<BatteryActionResult>> setChargeMode,
        Func<bool, Task<BatteryToggleResult>> setNightCharge,
        Func<bool, Task<FlipToStartResult>> setFlipToStart,
        Action killGpuApps,
        Action restartGpu,
        Func<bool, Task<StartupTaskResult>> setStartup,
        Action<string, string> showInfo,
        Action exitApp,
        bool fullWindow = false)
    {
        _fullWindow = fullWindow;
        _ownerHwnd = ownerHwnd;
        _setMode = setMode;
        _setChargeMode = setChargeMode;
        _setNightCharge = setNightCharge;
        _setFlipToStart = setFlipToStart;
        _killGpuApps = killGpuApps;
        _restartGpu = restartGpu;
        _setStartup = setStartup;
        _showInfo = showInfo;
        _exitApp = exitApp;

        _nightChargeSupported = nightChargeSnapshot.Supported;
        _flipToStartSupported = flipToStartSnapshot.Supported;
        _currentItsMode = snapshot.Mode;
        _currentChargeMode = batterySnapshot.Supported ? batterySnapshot.Mode : null;
        _nightChargeEnabled = nightChargeSnapshot.Enabled;
        _flipToStartEnabled = flipToStartSnapshot.Enabled;
        _startupEnabled = startupEnabled;

        Title = "Yoga控制中心";

        ExtendsContentIntoTitleBar = !_fullWindow;

        Content = BuildContent(
            modelName,
            itsCapabilities,
            snapshot,
            batterySnapshot,
            nightChargeSnapshot,
            flipToStartSnapshot,
            nvidiaGpuSupported);
        ConfigureWindow();
        SystemBackdrop = new MicaBackdrop();
        Activated += TrayMenuWindow_Activated;
        _appearanceSettings.ColorValuesChanged += AppearanceSettings_ColorValuesChanged;
        Closed += TrayMenuWindow_Closed;
    }

    public void ShowAtCursor()
    {
        uint dpi = GetWindowDpi();
        int width = ScaleDip(BaseWidthDip, dpi);
        int height = ScaleDip(GetDesiredHeightDip(), dpi);

        if (!GetCursorPos(out POINT cursor))
        {
            AppWindow.Resize(new SizeInt32(width, height));
            ShowMenu();
            return;
        }

        RectInt32 workArea = GetWorkArea(cursor);
        int edgeMargin = ScaleDip(5, dpi);
        int gap = ScaleDip(8, dpi);

        int x = cursor.X - width + ScaleDip(16, dpi);
        int y = cursor.Y - height - gap;

        int minX = workArea.X + edgeMargin;
        int maxX = workArea.X + workArea.Width - width - edgeMargin;
        x = maxX >= minX ? Math.Clamp(x, minX, maxX) : workArea.X;

        // Never shrink the window and never introduce scrolling. Keep the complete
        // menu visible whenever it fits the monitor work area. If a future machine
        // exposes so many capabilities that the natural menu becomes taller than
        // the work area, keep its full natural height rather than clipping content.
        int minY = workArea.Y + edgeMargin;
        int maxY = workArea.Y + workArea.Height - height - edgeMargin;
        if (maxY >= minY)
            y = Math.Clamp(y, minY, maxY);
        else
            y = workArea.Y;

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        ShowMenu();
    }

    public void ShowControlCenter()
    {
        uint dpi = GetWindowDpi();
        RectInt32 area = GetWorkArea(GetCursorPos(out POINT cursor) ? cursor : default);
        int width = Math.Min(ScaleDip(460, dpi), area.Width - ScaleDip(24, dpi));
        int height = Math.Min(ScaleDip(820, dpi), area.Height - ScaleDip(24, dpi));
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        Activate();
        ApplyDwmAppearance(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private int GetDesiredHeightDip()
    {
        // Measure the complete visual tree at its natural height. There is no
        // scrolling container: the AppWindow is resized to the content itself.
        _contentRoot?.Measure(new Windows.Foundation.Size(BaseWidthDip, 4096));

        double desired = (_contentRoot?.DesiredSize.Height ?? 548) + 12;
        if (double.IsFinite(desired) && desired > 0)
            return Math.Clamp((int)Math.Ceiling(desired), 120, 2048);

        return 548;
    }

    private void ShowMenu()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!_shown)
        {
            _shown = true;
            Activate();
        }
        ApplyDwmAppearance(hwnd);
        InstallOutsideClickHook();
    }

    private void TrayMenuWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_contentRoot is not Grid root || _closed) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            root.ClearValue(Grid.BackgroundProperty); // Theme-aware opaque fallback from the root style.
        else
            root.Background = TransparentBrush(); // Expose native Mica; system policy still controls fallback.
    }

    private StackPanel? _coreStack;
    private (bool Its, bool Fourth, bool Battery, bool Night, bool Flip, bool Gpu)? _capabilities;

    private UIElement BuildContent(
        string modelName,
        ItsCapabilitySnapshot itsCapabilities,
        ItsModeSnapshot snapshot,
        BatteryChargeSnapshot batterySnapshot,
        BatteryToggleSnapshot nightChargeSnapshot,
        FlipToStartSnapshot flipToStartSnapshot,
        bool nvidiaGpuSupported)
    {
        var outer = new Grid
        {
            Style = GetSettingsStyle("SettingsWindowRootStyle"),
            Padding = new Thickness(_fullWindow ? 20 : 9, _fullWindow ? 16 : 8, _fullWindow ? 20 : 9, 8)
        };
        _coreStack = new StackPanel { Spacing = _fullWindow ? 12 : 8 };
        PopulateCoreSections(_coreStack, modelName, itsCapabilities, snapshot,
            batterySnapshot, nightChargeSnapshot, flipToStartSnapshot, nvidiaGpuSupported);
        if (_fullWindow)
        {
            outer.Children.Add(new ScrollViewer
            {
                Content = _coreStack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        }
        else outer.Children.Add(_coreStack);
        _contentRoot = outer;
        return outer;
    }

    private void PopulateCoreSections(
        StackPanel stack, string modelName, ItsCapabilitySnapshot its,
        ItsModeSnapshot mode, BatteryChargeSnapshot battery,
        BatteryToggleSnapshot night, FlipToStartSnapshot flip, bool nvidia)
    {
        stack.Children.Clear();
        _modeTiles.Clear();
        _chargeModeTiles.Clear();
        _nightChargeVisual = _flipToStartVisual = _startupVisual = null;
        _nightChargeSupported = night.Supported;
        _flipToStartSupported = flip.Supported;
        _capabilities = (its.Supported, its.GeekModeSupported, battery.Supported, night.Supported, flip.Supported, nvidia);

        var header = new Grid { Margin = new Thickness(5, 2, 5, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Yoga控制中心", FontSize = _fullWindow ? 20 : 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var modelPill = CreatePill(modelName);
        Grid.SetColumn(modelPill, 1);
        header.Children.Add(modelPill);
        stack.Children.Add(header);

        void Section(string title, IReadOnlyList<UIElement> items, int columns, double height)
        {
            var section = new StackPanel { Spacing = 2 };
            section.Children.Add(CreateSectionHeader(title));
            section.Children.Add(CreateTileGrid(items, columns, height));
            stack.Children.Add(new Border
            {
                Style = GetSettingsStyle("SettingsCardStyle"),
                Child = section,
                Padding = new Thickness(_fullWindow ? 12 : 4)
            });
        }

        if (its.Supported)
        {
            var items = new List<UIElement>
            {
                CreateModeTile("智能模式", mode.Mode == ItsMode.Intelligent, ItsMode.Intelligent),
                CreateModeTile("安静模式", mode.Mode == ItsMode.Cool, ItsMode.Cool),
                CreateModeTile("性能模式", mode.Mode == ItsMode.Performance, ItsMode.Performance)
            };
            if (its.GeekModeSupported)
                items.Add(CreateModeTile(its.FourthModeDisplayName, mode.Mode == ItsMode.Creator, ItsMode.Creator));
            Section("性能选项", items, items.Count, 54);
        }

        if (battery.Supported)
        {
            var items = new List<UIElement>
            {
                CreateBatteryModeTile("养护模式", battery.Mode == BatteryChargeMode.Conservation, BatteryChargeMode.Conservation),
                CreateBatteryModeTile("常规模式", battery.Mode == BatteryChargeMode.Normal, BatteryChargeMode.Normal),
                CreateBatteryModeTile("快充模式", battery.Mode == BatteryChargeMode.RapidCharge, BatteryChargeMode.RapidCharge)
            };
            if (night.Supported)
            {
                _nightChargeVisual = CreateToggleTile("夜间优化", night.Enabled, NightChargeButton_Click, "夜间充电优化");
                items.Add(_nightChargeVisual.Button);
            }
            Section("充电选项", items, items.Count, 56);
        }

        if (nvidia)
            Section("独显休眠", new UIElement[]
            {
                CreateActionTile("结束独显占用进程", _killGpuApps),
                CreateActionTile("重启独显设备", _restartGpu)
            }, 2, 62);

        if (_fullWindow)
        {
            stack.Children.Add(AdditionalSections);
            var items = new List<UIElement>();
            if (flip.Supported)
            {
                _flipToStartVisual = CreateToggleTile("开盖开机", flip.Enabled, FlipToStartButton_Click, "开盖开机");
                items.Add(_flipToStartVisual.Button);
            }
            _startupVisual = CreateToggleTile("开机自启", _startupEnabled, StartupButton_Click, "开机自启动，无 UAC");
            items.Add(_startupVisual.Button);
            Section("杂项", items, items.Count, 56);
        }
        stack.Children.Add(CreateExitButton());
    }

    public void ApplyRefreshedState(AppRuntimeSnapshot state)
    {
        if (_closed)
            return;

        var capabilities = (state.ItsCapabilities.Supported, state.ItsCapabilities.GeekModeSupported,
            state.BatteryCharge.Supported, state.NightCharge.Supported, state.FlipToStart.Supported, state.NvidiaGpuSupported);
        if (_capabilities != capabilities && _coreStack is not null &&
            !_updatingItsMode && !_updatingChargeMode && !_updatingNightCharge && !_updatingFlipToStart && !_updatingStartup)
        {
            PopulateCoreSections(_coreStack, state.MachineInfo.DisplayModel, state.ItsCapabilities,
                state.ItsMode, state.BatteryCharge, state.NightCharge, state.FlipToStart, state.NvidiaGpuSupported);
            if (!_fullWindow) ShowAtCursor();
        }

        if (!_updatingItsMode && state.ItsMode.Mode is ItsMode currentMode)
        {
            _currentItsMode = currentMode;
            RefreshModeTiles();
        }

        if (!_updatingChargeMode && state.BatteryCharge.Supported)
        {
            _currentChargeMode = state.BatteryCharge.Mode;
            RefreshChargeModeTiles();
        }

        if (!_updatingNightCharge &&
            _nightChargeVisual is not null &&
            state.NightCharge.Supported)
        {
            _nightChargeEnabled = state.NightCharge.Enabled;
            RefreshToggleVisual(_nightChargeVisual, _nightChargeEnabled);
        }

        if (!_updatingFlipToStart &&
            _flipToStartVisual is not null &&
            state.FlipToStart.Supported)
        {
            _flipToStartEnabled = state.FlipToStart.Enabled;
            RefreshToggleVisual(_flipToStartVisual, _flipToStartEnabled);
        }

        if (!_updatingStartup && _startupVisual is not null)
        {
            _startupEnabled = state.StartupEnabled;
            RefreshToggleVisual(_startupVisual, _startupEnabled);
        }
    }

    private static Border CreatePill(string text)
    {
        var pill = new Border
        {
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(8),
            MaxWidth = 132
        };

        pill.Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        return pill;
    }

    private static Grid CreateSectionHeader(string title)
    {
        var header = new Grid
        {
            Margin = new Thickness(7, 1, 5, 2),
            MinHeight = 22
        };

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.62,
            VerticalAlignment = VerticalAlignment.Center
        });

        return header;
    }

    private Button CreateModeTile(
        string text,
        bool selected,
        ItsMode mode)
    {
        Button button = CreateOptionTile(text, selected, isAction: false, minHeight: 54);
        _modeTiles[mode] = button;
        button.Click += async (_, _) => await SetItsModeFromTileAsync(mode);
        return button;
    }

    private Button CreateBatteryModeTile(
        string text,
        bool selected,
        BatteryChargeMode mode)
    {
        Button button = CreateOptionTile(text, selected, isAction: false, minHeight: 54);
        _chargeModeTiles[mode] = button;
        button.Click += async (_, _) => await SetChargeModeFromTileAsync(mode);
        return button;
    }

    private async Task SetItsModeFromTileAsync(ItsMode requested)
    {
        if (_updatingItsMode || _currentItsMode == requested)
            return;

        _updatingItsMode = true;
        RefreshModeTiles(requested);

        try
        {
            ItsSetResult result = await _setMode(requested);
            if (result.Snapshot.Mode is ItsMode actual)
                _currentItsMode = actual;
        }
        finally
        {
            _updatingItsMode = false;
            if (!_closed)
                RefreshModeTiles();
        }
    }

    private async Task SetChargeModeFromTileAsync(BatteryChargeMode requested)
    {
        if (_updatingChargeMode || _currentChargeMode == requested)
            return;

        _updatingChargeMode = true;
        RefreshChargeModeTiles(requested);

        try
        {
            BatteryActionResult result = await _setChargeMode(requested);
            if (result.Snapshot.Supported)
                _currentChargeMode = result.Snapshot.Mode;
        }
        finally
        {
            _updatingChargeMode = false;
            if (!_closed)
                RefreshChargeModeTiles();
        }
    }

    private void RefreshModeTiles(ItsMode? pending = null)
    {
        foreach ((ItsMode mode, Button button) in _modeTiles)
        {
            RefreshOptionTile(
                button,
                selected: _currentItsMode == mode,
                pending: pending == mode);
        }
    }

    private void RefreshChargeModeTiles(BatteryChargeMode? pending = null)
    {
        foreach ((BatteryChargeMode mode, Button button) in _chargeModeTiles)
        {
            RefreshOptionTile(
                button,
                selected: _currentChargeMode == mode,
                pending: pending == mode);
        }
    }

    private TileVisual CreateToggleTile(
        string text,
        bool enabled,
        RoutedEventHandler click,
        string automationName)
    {
        Button button = CreateOptionTile(text, enabled, isAction: false, minHeight: 56);
        AutomationProperties.SetName(button, automationName);
        button.Click += click;

        var visual = new TileVisual
        {
            Button = button
        };

        RefreshToggleVisual(visual, enabled);
        return visual;
    }

    private static Button CreateActionTile(
        string text,
        Action action)
    {
        Button button = CreateOptionTile(text, false, isAction: true, minHeight: 62);
        button.Click += (_, _) => action();
        return button;
    }

    internal static Button CreateOptionTile(
        string text,
        bool selected,
        bool isAction,
        double minHeight)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = isAction ? 11 : 10.5,
            FontWeight = selected
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxLines = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new Grid();
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 8, 6, 8),
            MinHeight = minHeight,
            Style = GetSettingsStyle(selected ? "SettingsSelectedTileStyle" : "SettingsOptionTileStyle"),
            Margin = new Thickness(0),
            Tag = new OptionTileState
            {
                Label = label
            }
        };

        SetTileStateResources(button, selected);
        return button;
    }

    internal static Grid CreateTileGrid(
        IReadOnlyList<UIElement> items,
        int columns,
        double minHeight)
    {
        int actualColumns = Math.Max(1, columns);
        int rows = (items.Count + actualColumns - 1) / actualColumns;

        var grid = new Grid
        {
            Margin = new Thickness(5, 0, 5, 0)
        };

        for (int c = 0; c < actualColumns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        for (int r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
        }

        for (int i = 0; i < items.Count; i++)
        {
            UIElement item = items[i];
            int row = i / actualColumns;
            int col = i % actualColumns;

            var host = new Border
            {
                Margin = new Thickness(3),
                MinHeight = minHeight,
                Child = item as FrameworkElement
            };

            Grid.SetRow(host, row);
            Grid.SetColumn(host, col);
            grid.Children.Add(host);
        }

        return grid;
    }

    private Button CreateExitButton()
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = "退出",
                FontSize = 12.5
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(9, 5, 9, 5),
            MinHeight = 28,
            Margin = new Thickness(0, 2, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = TransparentBrush()
        };

        button.Click += (_, _) =>
        {
            Close();
            _exitApp();
        };

        return button;
    }

    private async void NightChargeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_nightChargeSupported || _updatingNightCharge || _nightChargeVisual is null)
            return;

        bool requested = !_nightChargeEnabled;
        _updatingNightCharge = true;
        _nightChargeVisual.Button.IsEnabled = false;
        _nightChargeEnabled = requested;
        RefreshToggleVisual(_nightChargeVisual, _nightChargeEnabled);

        try
        {
            BatteryToggleResult result = await _setNightCharge(requested);
            if (!result.Success)
            {
                _nightChargeEnabled = result.Snapshot.Supported
                    ? result.Snapshot.Enabled
                    : !requested;
                RefreshToggleVisual(_nightChargeVisual, _nightChargeEnabled);
                _showInfo("夜间充电优化设置失败", result.Message);
            }
            else
            {
                _nightChargeEnabled = result.Snapshot.Enabled;
                RefreshToggleVisual(_nightChargeVisual, _nightChargeEnabled);
            }
        }
        catch (Exception ex)
        {
            _nightChargeEnabled = !requested;
            RefreshToggleVisual(_nightChargeVisual, _nightChargeEnabled);
            _showInfo("夜间充电优化设置失败", ex.Message);
        }
        finally
        {
            _nightChargeVisual.Button.IsEnabled = true;
            _updatingNightCharge = false;
        }
    }

    private async void FlipToStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_flipToStartSupported || _updatingFlipToStart || _flipToStartVisual is null)
            return;

        bool requested = !_flipToStartEnabled;
        _updatingFlipToStart = true;
        _flipToStartVisual.Button.IsEnabled = false;
        _flipToStartEnabled = requested;
        RefreshToggleVisual(_flipToStartVisual, _flipToStartEnabled);

        try
        {
            FlipToStartResult result = await _setFlipToStart(requested);
            if (!result.Success)
            {
                _flipToStartEnabled = result.Snapshot.Supported
                    ? result.Snapshot.Enabled
                    : !requested;
                RefreshToggleVisual(_flipToStartVisual, _flipToStartEnabled);
                _showInfo("开盖开机设置失败", result.Message);
            }
            else
            {
                _flipToStartEnabled = result.Snapshot.Enabled;
                RefreshToggleVisual(_flipToStartVisual, _flipToStartEnabled);
            }
        }
        catch (Exception ex)
        {
            _flipToStartEnabled = !requested;
            RefreshToggleVisual(_flipToStartVisual, _flipToStartEnabled);
            _showInfo("开盖开机设置失败", ex.Message);
        }
        finally
        {
            _flipToStartVisual.Button.IsEnabled = true;
            _updatingFlipToStart = false;
        }
    }

    private async void StartupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingStartup || _startupVisual is null)
            return;

        bool requested = !_startupEnabled;
        _updatingStartup = true;
        _startupVisual.Button.IsEnabled = false;
        _startupEnabled = requested;
        RefreshToggleVisual(_startupVisual, _startupEnabled);

        try
        {
            StartupTaskResult result = await _setStartup(requested);
            if (!result.Success)
            {
                _startupEnabled = !requested;
                RefreshToggleVisual(_startupVisual, _startupEnabled);
                _showInfo("开机自启动设置失败", result.Message);
            }
        }
        catch (Exception ex)
        {
            _startupEnabled = !requested;
            RefreshToggleVisual(_startupVisual, _startupEnabled);
            _showInfo("开机自启动设置失败", ex.Message);
        }
        finally
        {
            _startupVisual.Button.IsEnabled = true;
            _updatingStartup = false;
        }
    }

    private static void RefreshToggleVisual(TileVisual visual, bool enabled) =>
        RefreshOptionTile(visual.Button, enabled);

    internal static void RefreshOptionTile(
        Button button,
        bool selected,
        bool pending = false)
    {
        if (button.Tag is not OptionTileState state)
            return;

        bool accentBorder = selected || pending;
        button.Style = GetSettingsStyle(accentBorder ? "SettingsSelectedTileStyle" : "SettingsOptionTileStyle");
        SetTileStateResources(button, accentBorder);

        state.Label.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private static void SetTileStateResources(Button button, bool selected)
    {
        // Keep the native button template and focus/disabled feedback, but never
        // let its pointer states replace the outline-only appearance with a fill.
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
            button.Resources["ButtonBackground" + state] = TransparentBrush();
        var outline = new SolidColorBrush(selected ? GetSystemAccentColor() : Color.FromArgb(110, 170, 170, 170));
        button.Resources["ButtonBorderBrushPointerOver"] = outline;
        button.Resources["ButtonBorderBrushPressed"] = outline;
    }

    internal static Style GetSettingsStyle(string key) => (Style)Application.Current.Resources[key];

    private void ConfigureWindow()
    {
        if (_fullWindow)
        {
            ExtendsContentIntoTitleBar = false;
            var fullPresenter = OverlappedPresenter.Create();
            fullPresenter.IsResizable = true;
            AppWindow.SetPresenter(fullPresenter);
            string fullIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "YogaITS.ico");
            if (File.Exists(fullIcon))
                AppWindow.SetIcon(fullIcon);
            return;
        }
        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        AppWindow.SetPresenter(presenter);

        string iconPath =
            Path.Combine(AppContext.BaseDirectory, "Assets", "YogaITS.ico");

        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Owned + TOOLWINDOW means:
        // - no taskbar button
        // - no Alt+Tab entry
        // The menu now accepts focus so native Mica follows its real activation.
        if (_ownerHwnd != 0)
            _ = SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, _ownerHwnd);

        nint exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long newExStyle =
            (exStyle.ToInt64() |
             WS_EX_TOOLWINDOW) &
            ~(WS_EX_APPWINDOW | WS_EX_NOACTIVATE);

        _ = SetWindowLongPtr(
            hwnd,
            GWL_EXSTYLE,
            new nint(newExStyle));

        _ = SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            SWP_NOMOVE |
            SWP_NOSIZE |
            SWP_NOZORDER |
            SWP_NOACTIVATE |
            SWP_FRAMECHANGED);
    }

    private void AppearanceSettings_ColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            ApplyDwmAppearance(WinRT.Interop.WindowNative.GetWindowHandle(this));
        });
    }

    private nint _mouseHook;
    private LowLevelMouseProc? _mouseHookProc;
    private bool _outsideCloseQueued;

    private void InstallOutsideClickHook()
    {
        if (_mouseHook != 0)
            return;

        _mouseHookProc = OutsideMouseHookProc;

        nint module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(
            WH_MOUSE_LL,
            _mouseHookProc,
            module,
            0);
    }

    private void UninstallOutsideClickHook()
    {
        if (_mouseHook == 0)
            return;

        _ = UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
        _mouseHookProc = null;
    }

    private nint OutsideMouseHookProc(
        int nCode,
        nint wParam,
        nint lParam)
    {
        if (nCode >= 0 &&
            IsMouseButtonDownMessage(
                unchecked((uint)wParam.ToInt64())))
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(
                    lParam);

                nint hwnd =
                    WinRT.Interop.WindowNative.GetWindowHandle(this);

                if (GetWindowRect(hwnd, out RECT rect) &&
                    !Contains(rect, data.pt) &&
                    !_outsideCloseQueued)
                {
                    _outsideCloseQueued = true;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            Close();
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
            }
        }

        return CallNextHookEx(
            _mouseHook,
            nCode,
            wParam,
            lParam);
    }

    private static bool IsMouseButtonDownMessage(uint message)
    {
        return message == WM_LBUTTONDOWN ||
               message == WM_RBUTTONDOWN ||
               message == WM_MBUTTONDOWN ||
               message == WM_XBUTTONDOWN;
    }

    private static bool Contains(
        RECT rect,
        POINT point)
    {
        return point.X >= rect.Left &&
               point.X < rect.Right &&
               point.Y >= rect.Top &&
               point.Y < rect.Bottom;
    }

    private void TrayMenuWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        _closed = true;
        Activated -= TrayMenuWindow_Activated;
        SystemBackdrop = null;
        _appearanceSettings.ColorValuesChanged -= AppearanceSettings_ColorValuesChanged;
        UninstallOutsideClickHook();
    }

    private uint GetWindowDpi()
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96u;
        }
    }

    private void TryEnableRoundedCorners()
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ApplyDwmAppearance(hwnd);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private static void ApplyDwmAppearance(nint hwnd)
    {
        // Mica is owned by Window.SystemBackdrop, not a competing DWM recipe.
        int preference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));

        // Always force the current Windows Accent Color onto our DWM border.
        // Do not follow the user's global "Show accent color on title bars and
        // window borders" switch. Without an explicit border color DWM can draw
        // a light/white non-client edge around this tool window.
        uint borderColor = GetAccentColorRef();

        _ = DwmSetWindowAttributeUInt(
            hwnd,
            DWMWA_BORDER_COLOR,
            ref borderColor,
            sizeof(uint));
    }

    private static Color GetSystemAccentColor()
    {
        try
        {
            var settings =
                new Windows.UI.ViewManagement.UISettings();

            return settings.GetColorValue(
                Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Color.FromArgb(255, 0, 120, 212);
        }
    }

    private static uint GetAccentColorRef()
    {
        Color accent = GetSystemAccentColor();

        // DWMWA_BORDER_COLOR expects COLORREF = 00BBGGRR.
        return accent.R |
               ((uint)accent.G << 8) |
               ((uint)accent.B << 16);
    }

    private static RectInt32 GetWorkArea(POINT point)
    {
        nint monitor = MonitorFromPoint(
            point,
            MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
        };

        if (GetMonitorInfo(monitor, ref info))
        {
            return new RectInt32(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);
        }

        return new RectInt32(0, 0, 1920, 1080);
    }

    private static int ScaleDip(
        int dip,
        uint dpi)
    {
        return (int)Math.Round(
            dip * dpi / 96.0);
    }

    private static SolidColorBrush TransparentBrush()
    {
        return new SolidColorBrush(
            Color.FromArgb(0, 0, 0, 0));
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    // DWM_COLOR_NONE = suppress the system-drawn border completely.
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;

    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    private const int WH_MOUSE_LL = 14;

    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_XBUTTONDOWN = 0x020B;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    private delegate nint LowLevelMouseProc(
        int nCode,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(
        out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(
        POINT pt,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint hMonitor,
        ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(
        nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(
        nint hWnd,
        int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint hWnd,
        int nIndex,
        nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        nint hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(
        nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hhk,
        int nCode,
        nint wParam,
        nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(
        string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint hWnd,
        out RECT lpRect);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeUInt(
        nint hwnd,
        int dwAttribute,
        ref uint pvAttribute,
        int cbAttribute);
}
