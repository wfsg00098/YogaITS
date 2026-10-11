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
    internal const double OptionTileHeightDip = 48;
    internal const double ControlCenterButtonFontSize = 12.5;
    private readonly bool _fullWindow;
    private readonly Windows.UI.ViewManagement.UISettings _appearanceSettings = new();
    internal StackPanel AdditionalSections { get; } = new() { Spacing = 8 };

    private readonly nint _ownerHwnd;
    private readonly Func<ItsMode, Task<ItsSetResult>> _setMode;
    private readonly Func<BatteryChargeMode, bool, Task<BatterySelectionResult>> _setChargeOption;
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
    private Grid? _windowHeader;
    private TextBlock? _modelLabel;
    private TextBlock? _performanceDescription;
    private TextBlock? _chargeDescription;
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
        Func<BatteryChargeMode, bool, Task<BatterySelectionResult>> setChargeOption,
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
        _setChargeOption = setChargeOption;
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

        ExtendsContentIntoTitleBar = true;

        Content = BuildContent(
            modelName,
            itsCapabilities,
            snapshot,
            batterySnapshot,
            nightChargeSnapshot,
            flipToStartSnapshot,
            nvidiaGpuSupported);
        ConfigureWindow();
        if (_fullWindow && _contentRoot is not null)
            _contentRoot.ActualThemeChanged += ContentRoot_ActualThemeChanged;
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
        ShowInForeground();
        ApplyDwmAppearance(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void ShowInForeground()
    {
        if (_closed) return;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd)) _ = ShowWindow(hwnd, 9); // SW_RESTORE; retain the previous size and position.
        Activate();
        // Activate alone can leave Explorer's tray panel in the foreground.
        // Request foreground activation only in the user's window-opening path.
        if (!SetForegroundWindow(hwnd))
            System.Diagnostics.Debug.WriteLine("Windows declined foreground activation for the settings window.");
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
            ShowInForeground();
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
            Padding = _fullWindow ? new Thickness(8) : new Thickness(9, 8, 9, 8)
        };
        _coreStack = new StackPanel { Spacing = 8 };
        PopulateCoreSections(_coreStack, modelName, itsCapabilities, snapshot,
            batterySnapshot, nightChargeSnapshot, flipToStartSnapshot, nvidiaGpuSupported);
        if (_fullWindow)
        {
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _windowHeader = CreateWindowHeader(modelName);
            _windowHeader.Loaded += (_, _) =>
            {
                UpdateHeaderInsets();
                ApplySystemControlButtonTheme();
            };
            _windowHeader.SizeChanged += (_, _) => UpdateHeaderInsets();
            outer.Children.Add(_windowHeader);
            var scroll = new ScrollViewer
            {
                Content = _coreStack,
                // Extend only the viewport into the existing right page padding.
                // Content still ends 8 DIP from the window edge.
                Margin = new Thickness(0, 0, -8, 0),
                Padding = new Thickness(0, 0, 8, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            scroll.Resources["ScrollViewerScrollBarMargin"] = new Thickness(0);
            scroll.Loaded += (_, _) => FitScrollBarToPagePadding(scroll);
            Grid.SetRow(scroll, 1);
            outer.Children.Add(scroll);
        }
        else outer.Children.Add(_coreStack);
        _contentRoot = outer;
        return outer;
    }

    private static void FitScrollBarToPagePadding(ScrollViewer scroll)
    {
        scroll.ApplyTemplate();
        var bar = FindVerticalScrollBar(scroll);
        if (bar is null) return;

        // Keep the native template, hover states and drag handling. Compress only
        // its horizontal rendering, anchored to the right, into the 8-DIP gutter.
        var scale = new ScaleTransform();
        bar.RenderTransformOrigin = new Windows.Foundation.Point(1, 0);
        bar.RenderTransform = scale;
        void UpdateWidth() => scale.ScaleX = bar.ActualWidth > 0
            ? Math.Min(1, 8 / bar.ActualWidth) : 1;
        bar.SizeChanged += (_, _) => UpdateWidth();
        UpdateWidth();
    }

    private static Microsoft.UI.Xaml.Controls.Primitives.ScrollBar? FindVerticalScrollBar(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar { Name: "VerticalScrollBar" } bar)
                return bar;
            if (FindVerticalScrollBar(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void PopulateCoreSections(
        StackPanel stack, string modelName, ItsCapabilitySnapshot its,
        ItsModeSnapshot mode, BatteryChargeSnapshot battery,
        BatteryToggleSnapshot night, FlipToStartSnapshot flip, bool nvidia)
    {
        stack.Children.Clear();
        _modeTiles.Clear();
        _chargeModeTiles.Clear();
        _performanceDescription = _chargeDescription = null;
        _nightChargeVisual = _flipToStartVisual = _startupVisual = null;
        _nightChargeSupported = night.Supported;
        _flipToStartSupported = flip.Supported;
        _capabilities = (its.Supported, its.GeekModeSupported, battery.Supported, night.Supported, flip.Supported, nvidia);

        if (_fullWindow)
        {
            if (_modelLabel is not null) _modelLabel.Text = modelName;
        }
        else stack.Children.Add(CreateWindowHeader(modelName));

        TextBlock? Section(string title, IReadOnlyList<UIElement> items, int columns, double height,
            string? description = null, bool divider = true, StackPanel? host = null)
        {
            if (_fullWindow)
            {
                height = OptionTileHeightDip;
                foreach (var button in items.OfType<Button>())
                {
                    button.MinHeight = height;
                    button.Padding = new Thickness(8);
                    if (button.Tag is OptionTileState tile) tile.Label.FontSize = ControlCenterButtonFontSize;
                }
            }
            var section = new StackPanel { Spacing = _fullWindow ? 8 : 2 };
            section.Children.Add(_fullWindow ? CreateGroupTitle(title) : CreateSectionHeader(title));
            section.Children.Add(CreateTileGrid(items, columns, height));
            TextBlock? note = null;
            if (_fullWindow && description is not null)
            {
                note = CreateDescription(description);
                note.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
                section.Children.Add(note);
            }
            var container = new Border
            {
                Style = GetSettingsStyle("SettingsCardStyle"),
                Child = section,
                Padding = new Thickness(_fullWindow ? 8 : 4)
            };
            if (!divider)
            {
                container.BorderThickness = new Thickness(0);
                container.Padding = _fullWindow ? new Thickness(8, 8, 8, 0) : new Thickness(4, 4, 4, 0);
            }
            (host ?? stack).Children.Add(container);
            return note;
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
            _performanceDescription = Section("性能选项", items, items.Count, 54, "");
            RefreshPerformanceDescription(mode.Mode);
        }

        if (battery.Supported)
        {
            var items = new List<UIElement>
            {
                CreateBatteryModeTile("养护模式", !night.Enabled && battery.Mode == BatteryChargeMode.Conservation, BatteryChargeMode.Conservation),
                CreateBatteryModeTile("常规模式", !night.Enabled && battery.Mode == BatteryChargeMode.Normal, BatteryChargeMode.Normal),
                CreateBatteryModeTile("智能快充", !night.Enabled && battery.Mode == BatteryChargeMode.RapidCharge, BatteryChargeMode.RapidCharge)
            };
            if (night.Supported)
            {
                _nightChargeVisual = CreateToggleTile("夜间优化", night.Enabled, NightChargeButton_Click, "夜间充电优化");
                items.Add(_nightChargeVisual.Button);
            }
            _chargeDescription = Section(_fullWindow ? "充电设置" : "充电选项", items, items.Count, 56,
                SettingsOptionText.ChargeDescription(battery.Mode.ToString(), night.Supported && night.Enabled));
        }

        if (nvidia)
            Section("独显休眠", new UIElement[]
            {
                CreateActionTile("结束独显占用进程", _killGpuApps),
                CreateActionTile("重启独显设备", _restartGpu)
            }, 2, 62,
                "结束占用独显的可处理进程，或重启独显设备以尝试恢复休眠。结束进程可能丢失未保存内容，重启设备可能导致屏幕短暂闪烁；实际休眠还取决于外接显示器及其他占用。");

        if (_fullWindow)
        {
            stack.Children.Add(AdditionalSections);
            if (flip.Supported)
            {
                _flipToStartVisual = CreateToggleTile("开盖开机", flip.Enabled, FlipToStartButton_Click, "开盖开机");
                Section("开盖开机", new UIElement[] { _flipToStartVisual.Button }, 1, OptionTileHeightDip,
                    "开启后，当您打开笔记本盖子时，系统将自动开机。（设置改变后，重启电脑方可生效）");
            }
        }
        // Both windows share the startup/exit group; lid-open startup remains
        // exclusive to the full control center and is not part of miscellaneous.
        _startupVisual = CreateToggleTile("开机自启", _startupEnabled, StartupButton_Click, "开机自启动，无 UAC");
        Section("杂项", new UIElement[] { _startupVisual.Button, CreateExitButton() }, 2,
            _fullWindow ? OptionTileHeightDip : 56, divider: false);
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
            RefreshChargeModeTiles();
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

    private Grid CreateWindowHeader(string modelName)
    {
        var header = new Grid
        {
            // Extend only the drag surface through the 8-DIP page padding.
            Margin = _fullWindow ? new Thickness(-8, -8, -8, 0) : new Thickness(5, 2, 5, 5),
            MinHeight = _fullWindow ? 32 : 0,
            Background = TransparentBrush()
        };
        var text = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        text.Children.Add(new TextBlock
        {
            Text = "Yoga控制中心", FontSize = _fullWindow ? 20 : 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (_fullWindow)
        {
            _modelLabel = new TextBlock
            {
                Text = modelName, FontSize = 12, Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            text.Children.Add(_modelLabel);
        }
        header.Children.Add(text);
        return header;
    }

    private void UpdateHeaderInsets()
    {
        if (!_fullWindow || _windowHeader?.XamlRoot is not { } root || _closed) return;
        double scale = root.RasterizationScale;
        // Padding belongs inside the drag element: its bounds now start at y=0,
        // while the heading keeps its existing position and avoids caption buttons.
        var padding = new Thickness(8 + AppWindow.TitleBar.LeftInset / scale, 8,
            8 + AppWindow.TitleBar.RightInset / scale, 0);
        if (!_windowHeader.Padding.Equals(padding)) _windowHeader.Padding = padding;
    }

    private void ControlCenterWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateHeaderInsets();
    }

    private void ContentRoot_ActualThemeChanged(FrameworkElement sender, object args) => ApplySystemControlButtonTheme();

    private void ApplySystemControlButtonTheme()
    {
        if (!_fullWindow || _closed || !AppWindowTitleBar.IsCustomizationSupported()) return;
        bool dark = _contentRoot?.ActualTheme == ElementTheme.Dark;
        Color foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 153, 153, 153) : Color.FromArgb(255, 102, 102, 102);
        titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(dark ? (byte)24 : (byte)16, foreground.R, foreground.G, foreground.B);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(dark ? (byte)16 : (byte)10, foreground.R, foreground.G, foreground.B);
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

    internal static TextBlock CreateGroupTitle(string title) => new()
    {
        Text = title,
        Style = GetSettingsStyle("SettingsSectionTitleStyle")
    };

    internal static TextBlock CreateDescription(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
    };

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

    private async Task SetChargeModeFromTileAsync(BatteryChargeMode requested, bool night = false)
    {
        if (_updatingChargeMode || (_currentChargeMode == requested && _nightChargeEnabled == night))
            return;

        _updatingChargeMode = _updatingNightCharge = true;
        foreach (var button in _chargeModeTiles.Values) button.IsEnabled = false;
        if (_nightChargeVisual is not null) _nightChargeVisual.Button.IsEnabled = false;
        RefreshChargeModeTiles(requested, night);

        try
        {
            BatterySelectionResult result = await _setChargeOption(requested, night);
            if (result.Charge.Supported) _currentChargeMode = result.Charge.Mode;
            if (result.Night.Supported) _nightChargeEnabled = result.Night.Enabled;
            if (!result.Success) _showInfo("充电设置失败", result.Message);
        }
        catch (Exception ex) { _showInfo("充电设置失败", ex.Message); }
        finally
        {
            _updatingChargeMode = _updatingNightCharge = false;
            if (!_closed)
            {
                foreach (var button in _chargeModeTiles.Values) button.IsEnabled = true;
                if (_nightChargeVisual is not null) _nightChargeVisual.Button.IsEnabled = true;
                RefreshChargeModeTiles();
            }
        }
    }

    private void RefreshModeTiles(ItsMode? pending = null)
    {
        RefreshPerformanceDescription(pending ?? _currentItsMode);
        foreach ((ItsMode mode, Button button) in _modeTiles)
        {
            RefreshOptionTile(
                button,
                selected: _currentItsMode == mode,
                pending: pending == mode);
        }
    }

    private void RefreshChargeModeTiles(BatteryChargeMode? pending = null, bool pendingNight = false)
    {
        RefreshChargeDescription(pending, pendingNight);
        foreach ((BatteryChargeMode mode, Button button) in _chargeModeTiles)
        {
            RefreshOptionTile(
                button,
                selected: !_nightChargeEnabled && _currentChargeMode == mode,
                pending: !pendingNight && pending == mode);
        }
        if (_nightChargeVisual is not null)
            RefreshOptionTile(_nightChargeVisual.Button, _nightChargeEnabled, pending is not null && pendingNight);
    }

    private void RefreshPerformanceDescription(ItsMode? mode)
    {
        if (_performanceDescription is null) return;
        string description = mode switch
        {
            ItsMode.Intelligent => "平衡性能与能耗，适合日常使用。",
            ItsMode.Cool => "侧重降低运行噪声，适合安静环境。",
            ItsMode.Performance => "侧重释放性能，适合高负载任务。",
            ItsMode.Creator => "面向创作或高性能任务的扩展性能模式。",
            _ => ""
        };
        _performanceDescription.Text = description;
        _performanceDescription.Visibility = description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshChargeDescription(BatteryChargeMode? pending = null, bool pendingNight = false)
    {
        if (_chargeDescription is null) return;
        _chargeDescription.Text = SettingsOptionText.ChargeDescription(
            (pending ?? _currentChargeMode)?.ToString(), pending is not null ? pendingNight : _nightChargeSupported && _nightChargeEnabled);
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
        double minHeight,
        double? fontSize = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize ?? (isAction ? 11 : 10.5),
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
            Padding = fontSize is null ? new Thickness(6, 8, 6, 8) : new Thickness(8),
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
        var button = CreateOptionTile("退出", false, true,
            _fullWindow ? OptionTileHeightDip : 56,
            _fullWindow ? ControlCenterButtonFontSize : null);

        button.Click += (_, _) =>
        {
            Close();
            _exitApp();
        };

        return button;
    }

    private async void NightChargeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nightChargeSupported)
            await SetChargeModeFromTileAsync(BatteryChargeMode.Normal, night: true);
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
            var fullPresenter = OverlappedPresenter.Create();
            fullPresenter.IsResizable = true;
            AppWindow.SetPresenter(fullPresenter);
            string fullIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "YogaITS.ico");
            if (File.Exists(fullIcon))
                AppWindow.SetIcon(fullIcon);
            SetTitleBar(_windowHeader);
            ApplySystemControlButtonTheme();
            AppWindow.Changed += ControlCenterWindow_Changed;
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
            ApplySystemControlButtonTheme();
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
        if (_fullWindow && _contentRoot is not null)
            _contentRoot.ActualThemeChanged -= ContentRoot_ActualThemeChanged;
        if (_fullWindow) AppWindow.Changed -= ControlCenterWindow_Changed;
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
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

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
