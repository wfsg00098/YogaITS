using YogaITS.Tray.UI.Accessibility;
using YogaITS.Tray.UI.Animation;
using YogaITS.Tray.UI.Controls;
using YogaITS.Tray.UI.Features;
using YogaITS.Tray.UI.Presentation;
#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Controls;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Settings;
using YogaITS.Tray.UI.Glass.Windowing;
#endif
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Updates;

namespace YogaITS.Tray.UI.Shell;

public sealed class TrayMenuWindow : Window
{
    private const int BaseWidthDip = 244;
    internal const double OptionTileHeightDip = 48;
    internal const double ControlCenterButtonFontSize = 12.5;
    internal const double MiniButtonFontSize = 10.5;
    internal const double OptionGridHorizontalMarginDip = 5;
    internal const double OptionTileMarginDip = 3;
    internal const double OptionTrackHorizontalMarginDip = OptionGridHorizontalMarginDip + OptionTileMarginDip;
    private readonly bool _fullWindow;
    private readonly Windows.UI.ViewManagement.UISettings _appearanceSettings = new();
    internal StackPanel AdditionalSections { get; } = new() { Spacing = 8 };
#if LIQUID_GLASS
    internal UiOptionsPanel? UiOptions { get; private set; }
#endif

    private readonly nint _ownerHwnd;
    private readonly Func<ItsMode, Task<ItsSetResult>> _setMode;
    private readonly Func<BatteryChargeMode, bool, Task<BatterySelectionResult>> _setChargeOption;
    private readonly Func<bool, Task<FlipToStartResult>> _setFlipToStart;
    private readonly Func<Task> _killGpuApps;
    private readonly Func<Task> _restartGpu;
    private bool _updatingGpuAction;
    private GpuKeepAwakeSnapshot _keepAwake = new(false, false, "");
    private readonly Func<bool, Task<GpuKeepAwakeSnapshot>>? _setKeepAwake;
    private Button? _keepAwakeButton, _restartGpuButton;
    private TextBlock? _gpuDescription;
    private bool _updatingKeepAwake;
    private readonly Func<bool, Task<StartupTaskResult>> _setStartup;
    private readonly Action<string, string> _showInfo;
    private readonly Action _exitApp;

    private bool _nightChargeSupported;
    private bool _flipToStartSupported;

    private TileVisual? _flipToStartVisual;
    private TileVisual? _startupVisual;

    private HardwareChoiceGroup? _performanceChoices, _chargeChoicesUi;
    private bool _optionResizeQueued;
    private ItsMode[] _performanceModes = [];
    private string[] _performanceLabels = [];
    private (BatteryChargeMode Mode, bool Night)[] _chargeChoices = [];
    private bool _batteryAvailable = true, _nightAvailable = true;

    private FrameworkElement? _contentRoot;
    private Grid? _windowHeader;
    private TransientScrollBar? _transientScrollBar;
    private Action? _updateScrollWidth;
    private TextBlock? _modelLabel;
    private ContentControl? _titleFocusTarget;
    private TextBlock? _performanceDescription;
    private TextBlock? _chargeDescription;
    private TextBlock? _flipToStartDescription;
    private bool _flipToStartOperated;
    private bool _closed;
    private bool _shown;
    private bool _isActive;
    private int _powerSource = SystemEventMonitor.ReadPowerSource();

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
    private Border? _updateHost;
    private Button? _updateButton;
    private Grid? _updateFollowingTiles;
    private Func<Task>? _installUpdate;


    private sealed class TileVisual
    {
        public required Button Button { get; init; }
    }

    private sealed class OptionTileState
    {
        public required TextBlock Label { get; init; }
        public required SolidColorBrush Outline { get; init; }
        public bool Accent { get; set; }
        public bool IsAction { get; set; }
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
        Func<Task> killGpuApps,
        Func<Task> restartGpu,
        Func<bool, Task<StartupTaskResult>> setStartup,
        Action<string, string> showInfo,
        Action exitApp,
        bool fullWindow = false,
        GpuKeepAwakeSnapshot? keepAwake = null,
        Func<bool, Task<GpuKeepAwakeSnapshot>>? setKeepAwake = null)
    {
        _fullWindow = fullWindow;
        _ownerHwnd = ownerHwnd;
        _setMode = setMode;
        _setChargeOption = setChargeOption;
        _setFlipToStart = setFlipToStart;
        _killGpuApps = killGpuApps;
        _restartGpu = restartGpu;
        _keepAwake = keepAwake ?? _keepAwake;
        _setKeepAwake = setKeepAwake;
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

        Title = AppIdentity.DisplayName;

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
#if LIQUID_GLASS
        if (Content is FrameworkElement materialRoot)
            materialRoot.Loaded += async (_, _) =>
            {
                if (_closed) return;
                try { await GlassSettingsController.EnsureInitialStyleAsync(); }
                catch (Exception ex) { CaptureDiagnostics.Write("Initial UI style: " + ex.Message); }
            };
#endif
        if (_fullWindow && _contentRoot is not null)
            _contentRoot.ActualThemeChanged += ContentRoot_ActualThemeChanged;
        WindowMaterial.Attach(this, (Grid)_contentRoot!);
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
        int width = Math.Min(ScaleDip(492, dpi), area.Width - ScaleDip(24, dpi));
        int height = Math.Min(ScaleDip(820, dpi), area.Height - ScaleDip(24, dpi));
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        ShowInForeground();
        // Start on the title rather than a function tile. Tab then advances to
        // the first available button while retaining normal keyboard visuals.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closed || !_isActive) return;
            if (_titleFocusTarget is { } title)
            {
                title.IsTabStop = true;
                title.Focus(FocusState.Programmatic);
            }
        });
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
        _performanceChoices?.RefreshPresentation();
        _chargeChoicesUi?.RefreshPresentation();
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
        if (_closed) return;
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_fullWindow)
            ApplyDwmAppearance(WinRT.Interop.WindowNative.GetWindowHandle(this));
        // WindowMaterial owns the active/fallback transition. An opaque
        // root on deactivation would cover that transition with an instant fill.
    }

    private StackPanel? _coreStack;
    private bool _itsAvailable = true;
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
        _coreStack = new StackPanel
        {
            Spacing = 8,
            // Keep the bottom layout inset inside the scrollable content, so
            // shadows may paint through it without hitting the viewport edge.
            Margin = new Thickness(0, 0, 0, _fullWindow ? 8 : 0)
        };
        SettingsMotion.Reposition(_coreStack);
        SettingsMotion.Reposition(AdditionalSections);
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
                // Extend the viewport into the right and bottom page padding.
                // The stack's bottom margin preserves the original 8-DIP inset;
                // only the clipping boundary moves to the window bottom.
                Margin = new Thickness(0, 0, -8, -8),
                Padding = new Thickness(0, 0, 8, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                IsVerticalRailEnabled = true,
                IsScrollInertiaEnabled = true,
                ZoomMode = ZoomMode.Disabled
            };
            scroll.Resources["ScrollViewerScrollBarMargin"] = new Thickness(0);
            scroll.Loaded += (_, _) => FitScrollBarToPagePadding(scroll);
            Grid.SetRow(scroll, 1);
            outer.Children.Add(scroll);
        }
        else outer.Children.Add(_coreStack);
        _contentRoot = outer;
        outer.KeyDown += (_, args) =>
        {
            if (!_fullWindow && args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                Close();
            }
        };
        return outer;
    }

    private void FitScrollBarToPagePadding(ScrollViewer scroll)
    {
        scroll.ApplyTemplate();
        var bar = FindVerticalScrollBar(scroll);
        if (bar is null) return;

        // Keep the native template, hover states and drag handling. Compress only
        // its horizontal rendering, anchored to the right, into the 8-DIP gutter.
        var scale = new ScaleTransform();
        bar.RenderTransformOrigin = new Windows.Foundation.Point(1, 0);
        bar.RenderTransform = scale;
        void UpdateWidth()
        {
            bool compact = _appearanceSettings.AutoHideScrollBars;
            scale.ScaleX = compact && bar.ActualWidth > 0 ? Math.Min(1, 8 / bar.ActualWidth) : 1;
            double padding = compact ? 8 : Math.Max(8, bar.ActualWidth + 8);
            if (scroll.Padding.Right != padding) scroll.Padding = new Thickness(0, 0, padding, 0);
        }
        _updateScrollWidth = UpdateWidth;
        bar.SizeChanged += (_, _) => UpdateWidth();
        UpdateWidth();
        _transientScrollBar?.Dispose();
        _transientScrollBar = new TransientScrollBar(scroll, bar);
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
#if LIQUID_GLASS
        UiOptions?.Dispose();
        UiOptions=null;
#endif
        stack.Children.Clear();
        _performanceChoices?.Dispose();
        _chargeChoicesUi?.Dispose();
        _performanceChoices = _chargeChoicesUi = null;
        _performanceDescription = _chargeDescription = null;
        _flipToStartVisual = _startupVisual = null;
        _nightChargeSupported = night.Supported;
        _flipToStartSupported = flip.Supported;
        _capabilities = (its.Supported, its.GeekModeSupported, battery.Supported, night.Supported, flip.Supported, nvidia);

        if (_fullWindow)
        {
            if (_modelLabel is not null) _modelLabel.Text = modelName;
        }
        else stack.Children.Add(CreateWindowHeader(modelName));

        TextBlock? Section(string title, IReadOnlyList<UIElement> items, int columns, double height,
            string? description = null, bool divider = true, StackPanel? host = null,
            bool stretchLastRow = false, UIElement? leadingContent = null)
        {
            foreach (var button in items.OfType<Button>())
                if (button.Tag is OptionTileState option)
                    AutomationProperties.SetName(button, UiAccessibility.GroupedName(title, option.Label.Text));
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
            SettingsMotion.Reposition(section);
            section.Children.Add(_fullWindow ? CreateGroupTitle(title) : CreateSectionHeader(title));
            UIElement tiles = items.Count == 1 && items[0] is OptionGroupPresentation or AnimatedOptionHost
                ? items[0] : CreateTileGrid(items, columns, height);
            if (stretchLastRow && items.Count % columns == 1 && tiles is Grid grid && grid.Children.Count > 0)
                Grid.SetColumnSpan((FrameworkElement)grid.Children[grid.Children.Count - 1], columns);
            if (leadingContent is null) section.Children.Add(tiles);
            else
            {
                var body = new StackPanel();
                body.Children.Add(leadingContent);
                body.Children.Add(tiles);
                _updateFollowingTiles = (Grid)tiles;
                SettingsMotion.RevealBefore(leadingContent, (FrameworkElement)tiles);
                section.Children.Add(body);
            }
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
            else SettingsMotion.SeparateDivider(container);
            (host ?? stack).Children.Add(container);
            return note;
        }

        if (its.Supported)
        {
            _performanceModes = its.GeekModeSupported
                ? [ItsMode.Intelligent, ItsMode.Cool, ItsMode.Performance, ItsMode.Creator]
                : [ItsMode.Intelligent, ItsMode.Cool, ItsMode.Performance];
            string[] names = its.GeekModeSupported
                ? ["智能模式", "安静模式", "性能模式", its.FourthModeDisplayName]
                : ["智能模式", "安静模式", "性能模式"];
            _performanceLabels = names;
            _performanceChoices = CreateHardwareChoices("性能选项", 54);
            _performanceDescription = Section("性能选项", [_performanceChoices.View], 1, 80, "");
            RefreshPerformanceTabs();
        }

        if (battery.Supported)
        {
            _chargeChoices = night.Supported
                ? [(BatteryChargeMode.Conservation,false), (BatteryChargeMode.Normal,false),
                   (BatteryChargeMode.RapidCharge,false), (BatteryChargeMode.Normal,true)]
                : [(BatteryChargeMode.Conservation,false), (BatteryChargeMode.Normal,false),
                   (BatteryChargeMode.RapidCharge,false)];
            string[] names = night.Supported
                ? ["养护模式", "常规模式", "智能快充", "夜间优化"]
                : ["养护模式", "常规模式", "智能快充"];
            string title = _fullWindow ? "充电设置" : "充电选项";
            _chargeChoicesUi = CreateHardwareChoices(title, 56);
            _chargeDescription = Section(title, [_chargeChoicesUi.View], 1, 80,
                SettingsOptionText.ChargeDescription(battery.Mode.ToString(), night.Supported && night.Enabled));
            RefreshChargeTabs();
        }

        _keepAwakeButton = _restartGpuButton = null;
        _gpuDescription = null;
        if (nvidia)
        {
            _restartGpuButton = CreateActionTile("重启独显设备", _restartGpu);
            var gpuItems = new List<UIElement>
            {
                CreateActionTile(_fullWindow ? "结束独显占用进程" : "结束独显\n占用进程", _killGpuApps,
                    delayBeforeActionMilliseconds: 500),
                _restartGpuButton
            };
            if (_fullWindow)
            {
                _keepAwakeButton = CreateOptionTile("保持独显唤醒", _keepAwake.Enabled, false,
                    OptionTileHeightDip, ControlCenterButtonFontSize);
                _keepAwakeButton.Click += async (_, _) => await SetKeepAwakeAsync();
                gpuItems.Add(_keepAwakeButton);
            }
            _gpuDescription = Section("独显控制", gpuItems, _fullWindow ? 3 : 2, 62,
                GpuDescription(), stretchLastRow: !_fullWindow);
            ApplyKeepAwakeState(_keepAwake);
        }

        if (_fullWindow)
        {
            stack.Children.Add(AdditionalSections);
            if (flip.Supported)
            {
                _flipToStartVisual = CreateToggleTile("开盖开机", flip.Enabled, FlipToStartButton_Click, "开盖开机");
                _flipToStartDescription = Section("开盖开机", new UIElement[] { _flipToStartVisual.Button }, 1, OptionTileHeightDip,
                    SettingsOptionText.FlipToStartDescription(_flipToStartOperated));
            }
#if LIQUID_GLASS
            UiOptions=new UiOptionsPanel(this);
            stack.Children.Add(UiOptions.View);
#endif
            _startupVisual = CreateToggleTile("开机自启", _startupEnabled, StartupButton_Click, "开机自启动，无 UAC");
            _updateButton = CreateOptionTile("", false, true, OptionTileHeightDip, ControlCenterButtonFontSize);
            _updateButton.Click += async (_, _) =>
            {
                if (_installUpdate is not null) await _installUpdate();
            };
            _updateHost = new Border
            {
                Visibility = Visibility.Collapsed,
                Child = CreateTileGrid(new UIElement[] { _updateButton }, 1, OptionTileHeightDip)
            };
            Section("杂项", new UIElement[] { _startupVisual.Button, CreateExitButton() }, 2,
                OptionTileHeightDip, divider: false, leadingContent: _updateHost);
        }
        else stack.Children.Add(new Border
        {
            // Reuse the sections' horizontal padding and tile-grid margins so
            // the single exit tile aligns with the outer edges of their tiles.
            Padding = new Thickness(4, 0, 4, 0),
            Child = CreateTileGrid(new UIElement[] { CreateExitButton() }, 1, 28)
        });
    }

#if LIQUID_GLASS
    internal void RefreshUiOptions()=>UiOptions?.Refresh();
#endif

    public void ApplyKeepAwakeState(GpuKeepAwakeSnapshot state)
    {
        if (_closed) return;
        _keepAwake = state;
        if (_restartGpuButton is { } restart) restart.IsEnabled = !_updatingKeepAwake;
        if (_keepAwakeButton is { } keep)
        {
            keep.IsEnabled = _setKeepAwake is not null;
            if (!_updatingKeepAwake) RefreshOptionTile(keep, state.Enabled);
        }
        if (_gpuDescription is { } description) description.Text = GpuDescription();
    }

    private string GpuDescription() =>
        "结束进程可能丢失未保存内容，重启设备可能导致屏幕短暂闪烁。保持唤醒可减少独显休眠后唤醒的卡顿，但会增加功耗；实际空闲功耗状态由驱动决定。" +
        (_keepAwake.Enabled && _keepAwake.Message.Length > 0 ? "\n" + _keepAwake.Message : "");

    private async Task SetKeepAwakeAsync()
    {
        if (_closed || _updatingKeepAwake || _updatingGpuAction || _setKeepAwake is null || _keepAwakeButton is null) return;
        _updatingKeepAwake = true;
        RefreshOptionTile(_keepAwakeButton, _keepAwake.Enabled, pending: true);
        if (_restartGpuButton is { } restart) restart.IsEnabled = false;
        try { _keepAwake = await _setKeepAwake(!_keepAwake.Enabled); }
        catch (Exception ex) { if (!_closed) _showInfo("保持独显唤醒设置失败", ex.Message); }
        finally { _updatingKeepAwake = false; ApplyKeepAwakeState(_keepAwake); }
    }

    internal void SetAvailableUpdate(UpdateRelease? release, bool busy, Func<Task> install)
    {
        if (_closed || !_fullWindow || _updateHost is null || _updateButton is null) return;
        _installUpdate = install;
        if (_updateFollowingTiles is { } following)
            SettingsMotion.RevealBefore(_updateHost, following);
        if (release is null)
        {
            _updateHost.Visibility = Visibility.Collapsed;
            return;
        }
        string text = $"新版本可用，点击进行更新\n{UpdatePolicy.CurrentVersion} -> {release.Version}";
        ((OptionTileState)_updateButton.Tag).Label.Text = text;
        AutomationProperties.SetName(_updateButton, UiAccessibility.GroupedName("杂项", text));
        RefreshOptionTile(_updateButton, false, pending: busy);
        // Populate content before making it participate in layout.
        _updateHost.Visibility = Visibility.Visible;
    }

    internal async Task<bool> ConfirmGpuRestartAsync()
    {
        if (_closed || _contentRoot?.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = _contentRoot.XamlRoot,
            Title = "重启独显设备",
            Content = "屏幕可能短暂闪烁，正在使用独显的应用可能受到影响。Yoga控制中心将退出，并在设备重启后自动重新打开。是否继续？",
            PrimaryButtonText = "重启", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && !_closed; }
        catch (Exception) when (_closed) { return false; }
    }

    internal async Task<bool> ConfirmUpdateAsync(string version)
    {
        if (_closed || _contentRoot?.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = _contentRoot.XamlRoot,
            Title = "更新 Yoga控制中心",
            Content = $"是否下载并安装 {version}？下载并验证完成后，程序将退出、更新并重新启动。配置与校色文件会保留。",
            PrimaryButtonText = "更新", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void ApplyRefreshedState(AppRuntimeSnapshot state)
    {
        if (_closed)
            return;
        if (_modelLabel is not null) _modelLabel.Text = state.MachineInfo.DisplayModel;
        _powerSource = SystemEventMonitor.ReadPowerSource();
        _itsAvailable = state.ItsAvailable;
        _batteryAvailable = state.BatteryAvailable;
        _nightAvailable = state.NightAvailable;
        if (_coreStack is not null) SettingsMotion.Reposition(_coreStack);
        if (_coreStack is not null)
            foreach (var group in _coreStack.Children.OfType<Border>())
                SettingsMotion.RepositionGroup(group);
        SettingsMotion.Reposition(AdditionalSections);

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
            RefreshPerformanceTabs();
        }

        bool chargeChanged = false;
        if (!_updatingChargeMode && state.BatteryCharge.Supported)
        {
            _currentChargeMode = state.BatteryCharge.Mode;
            chargeChanged = true;
        }

        if (!_updatingNightCharge &&
            state.NightCharge.Supported)
        {
            _nightChargeEnabled = state.NightCharge.Enabled;
            chargeChanged = true;
        }
        if (chargeChanged) RefreshChargeTabs();

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
        // Retain last confirmed selections on transient read failures, but never
        // let keyboard or pointer input operate using a stale hardware snapshot.
        if (!_updatingItsMode) RefreshPerformanceTabs();
        if (!_updatingChargeMode && !_updatingNightCharge) RefreshChargeTabs();
        if (_flipToStartVisual is { } flip) flip.Button.IsEnabled = state.FlipAvailable;
        if (_startupVisual is { } startup) startup.Button.IsEnabled = state.StartupAvailable;
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
            Text = AppIdentity.DisplayName, FontSize = _fullWindow ? 20 : 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (_fullWindow)
        {
            _modelLabel = new TextBlock
            {
                Text = modelName, Style = GetSettingsStyle("SettingsSecondaryTextStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            text.Children.Add(_modelLabel);
        }
        if (_fullWindow)
        {
            _titleFocusTarget = new ContentControl
            {
                Content = text,
                IsTabStop = true,
                UseSystemFocusVisuals = false,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            header.Children.Add(_titleFocusTarget);
            _titleFocusTarget.LostFocus += (_, _) => _titleFocusTarget.IsTabStop = false;
        }
        else header.Children.Add(text);
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
        if (UiAccessibility.HighContrast) foreground = UiAccessibility.SystemColor(8);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = UiAccessibility.HighContrast ? foreground :
            dark ? Color.FromArgb(255, 153, 153, 153) : Color.FromArgb(255, 102, 102, 102);
        titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(dark ? (byte)24 : (byte)16, foreground.R, foreground.G, foreground.B);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(dark ? (byte)16 : (byte)10, foreground.R, foreground.G, foreground.B);
        if (UiAccessibility.HighContrast)
        {
            titleBar.ButtonHoverBackgroundColor = titleBar.ButtonPressedBackgroundColor = UiAccessibility.SystemColor(13);
            titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = UiAccessibility.SystemColor(14);
        }
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
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = GetSettingsStyle("SettingsSecondaryTextStyle"),
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
        Text = text, TextWrapping = TextWrapping.Wrap, Style = GetSettingsStyle("SettingsSecondaryTextStyle")
    };

    private HardwareChoiceGroup CreateHardwareChoices(string title, double miniHeight)
    {
        var choices = new HardwareChoiceGroup(this, title,
            () => { RefreshPerformanceTabs(); RefreshChargeTabs(); },
            error => _showInfo(title + "设置失败", error.Message),
            _fullWindow ? OptionTileHeightDip : miniHeight,
            _fullWindow ? ControlCenterButtonFontSize : MiniButtonFontSize, compact: !_fullWindow);
        choices.PresentationChanged += () =>
        {
            if (_fullWindow || !_shown || _optionResizeQueued) return;
            _optionResizeQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _optionResizeQueued = false;
                if (!_closed) ShowAtCursor();
            });
        };
        return choices;
    }

    private async Task SetItsModeAsync(ItsMode requested)
    {
        if (!_itsAvailable || requested == ItsMode.Creator && !ItsCapabilityPolicy.CanSelectFourthMode(SystemEventMonitor.ReadPowerSource())) return;
        if (_updatingItsMode || _currentItsMode == requested)
            return;

        _updatingItsMode = true;
        RefreshPerformanceTabs();

        try
        {
            ItsSetResult result = await _setMode(requested);
            if (result.Snapshot.Mode is ItsMode actual)
                _currentItsMode = actual;
        }
        catch (Exception ex) { if (!_closed) _showInfo("性能设置失败", ex.Message); }
        finally
        {
            _updatingItsMode = false;
            if (!_closed)
                RefreshPerformanceTabs();
        }
    }

    private async Task SetChargeOptionAsync(BatteryChargeMode requested, bool night = false)
    {
        if (_updatingChargeMode || (_currentChargeMode == requested && _nightChargeEnabled == night))
            return;

        _updatingChargeMode = _updatingNightCharge = true;
        RefreshChargeTabs();

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
                RefreshChargeTabs();
            }
        }
    }

    private void RefreshPerformanceTabs()
    {
        RefreshPerformanceDescription(_currentItsMode);
        _performanceChoices?.Update(_performanceModes.Select((mode, index) => new HardwareChoice(
            mode.ToString(), _performanceLabels[index], () => SetItsModeAsync(mode),
            mode != ItsMode.Creator || ItsCapabilityPolicy.CanSelectFourthMode(_powerSource))),
            _currentItsMode?.ToString(), _itsAvailable, _performanceModes.Length);
    }

    public void RefreshPowerSource()
    {
        if (_closed) return;
        _powerSource = SystemEventMonitor.ReadPowerSource();
        if (!_updatingItsMode) RefreshPerformanceTabs();
    }

    private void RefreshChargeTabs()
    {
        RefreshChargeDescription();
        string? confirmed = _currentChargeMode is { } mode
            ? $"{(_nightChargeSupported && _nightChargeEnabled ? BatteryChargeMode.Normal : mode)}:{(_nightChargeSupported && _nightChargeEnabled)}" : null;
        _chargeChoicesUi?.Update(_chargeChoices.Select(choice => new HardwareChoice(
            $"{choice.Mode}:{choice.Night}", choice.Night ? "夜间优化" : choice.Mode switch
            {
                BatteryChargeMode.Conservation => "养护模式", BatteryChargeMode.Normal => "常规模式", _ => "智能快充"
            }, () => SetChargeOptionAsync(choice.Mode, choice.Night))), confirmed,
            _batteryAvailable && (!_nightChargeSupported || _nightAvailable), _chargeChoices.Length);
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

    private void RefreshChargeDescription()
    {
        if (_chargeDescription is null) return;
        _chargeDescription.Text = SettingsOptionText.ChargeDescription(
            _currentChargeMode?.ToString(), _nightChargeSupported && _nightChargeEnabled);
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

    private Button CreateActionTile(
        string text,
        Func<Task> action,
        int delayBeforeActionMilliseconds = 0)
    {
        Button button = CreateOptionTile(text, false, isAction: true, minHeight: 62);
        button.Click += async (_, _) =>
        {
            if (_updatingGpuAction || _closed) return;
            _updatingGpuAction = true;
            RefreshOptionTile(button, false, pending: true);
            try
            {
                // Yield to the dispatcher so the pending outline is rendered first.
                if (delayBeforeActionMilliseconds > 0)
                    await Task.Delay(delayBeforeActionMilliseconds);
                if (!_closed) await action();
            }
            catch (Exception ex) { if (!_closed) _showInfo("独显休眠操作失败", ex.Message); }
            finally
            {
                _updatingGpuAction = false;
                if (!_closed) RefreshOptionTile(button, false);
            }
        };
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
            FontSize = fontSize ?? (isAction ? 11 : MiniButtonFontSize),
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new Grid();
        content.Children.Add(label);

        var outline = new SolidColorBrush(GetTileOutlineColor(selected));
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // DefaultButtonStyle uses Center. Fill the shared Auto row instead
            // so wrapped labels cannot leave neighboring tiles shorter.
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = fontSize is null ? new Thickness(6, 8, 6, 8) : new Thickness(8),
            MinHeight = minHeight,
            Style = GetSettingsStyle("SettingsOptionTileStyle"),
            BorderBrush = outline,
            Margin = new Thickness(0),
            Tag = new OptionTileState
            {
                Label = label, Outline = outline, Accent = selected, IsAction = isAction
            }
        };

        SetTileStateResources(button, outline);
        SettingsMotion.AttachHover(button);
        WindowMaterial.AttachTile(button, content, selected);
        AutomationProperties.SetName(button, text.Replace("\n", ""));
        if (!isAction) UiAccessibility.SetState(button, selected ? "已选中" : "未选中");
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
            Margin = new Thickness(OptionGridHorizontalMarginDip, 0, OptionGridHorizontalMarginDip, 0)
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
                Margin = new Thickness(OptionTileMarginDip),
                MinHeight = minHeight,
                Child = item as FrameworkElement
            };

            Grid.SetRow(host, row);
            Grid.SetColumn(host, col);
            grid.Children.Add(host);
        }
        SettingsMotion.Reposition(grid);
        return grid;
    }

    private Button CreateExitButton()
    {
        var button = CreateOptionTile("退出", false, true,
            _fullWindow ? OptionTileHeightDip : 28,
            _fullWindow ? ControlCenterButtonFontSize : null);
        if (!_fullWindow) button.Padding = new Thickness(6, 4, 6, 4);
        AutomationProperties.SetName(button, UiAccessibility.GroupedName("Yoga控制中心", "退出"));

        button.Click += (_, _) =>
        {
            Close();
            _exitApp();
        };

        return button;
    }

    private async void FlipToStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_flipToStartSupported || _updatingFlipToStart || _flipToStartVisual is null)
            return;

        bool requested = !_flipToStartEnabled;
        _updatingFlipToStart = true;
        _flipToStartOperated = true;
        if (_flipToStartDescription is not null)
            _flipToStartDescription.Text = SettingsOptionText.FlipToStartDescription(true);
        RefreshOptionTile(_flipToStartVisual.Button, _flipToStartEnabled, pending: true);
        _flipToStartEnabled = requested;

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
            _updatingFlipToStart = false;
            if (!_closed) RefreshToggleVisual(_flipToStartVisual, _flipToStartEnabled);
        }
    }

    private async void StartupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingStartup || _startupVisual is null)
            return;

        bool requested = !_startupEnabled;
        _updatingStartup = true;
        RefreshOptionTile(_startupVisual.Button, _startupEnabled, pending: true);

        try
        {
            StartupTaskResult result = await _setStartup(requested);
            _startupEnabled = result.Enabled ?? (result.Success ? requested : _startupEnabled);
            if (!result.Success)
            {
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
            _updatingStartup = false;
            if (!_closed) RefreshToggleVisual(_startupVisual, _startupEnabled);
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

        // Native pointer states may already hold their resolved resource brush.
        // Mutate that shared instance instead of replacing resources/styles:
        // normal, hovered and pressed borders update without pointer movement.
        Color outlineColor = GetTileOutlineColor(selected || pending);
        state.Accent = selected || pending;
        if (state.Outline.Color != outlineColor) state.Outline.Color = outlineColor;
        WindowMaterial.SetTileState(button, state.Accent);
        UiAccessibility.SetState(button, pending ? "正在设置" : state.IsAction ? "" : selected ? "已选中" : "未选中");

        state.Label.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
    }

    internal static void SetOptionText(Button button, string text)
    {
        if (button.Tag is OptionTileState state) state.Label.Text = text;
    }

    internal static void UpdateOptionLabel(Button target, Button source)
    {
        if (target.Tag is OptionTileState retained && source.Tag is OptionTileState candidate &&
            retained.Label.Text != candidate.Label.Text)
            retained.Label.Text = candidate.Label.Text;
        if (target.Tag is OptionTileState targetState && source.Tag is OptionTileState sourceState)
            targetState.IsAction = sourceState.IsAction;
        AutomationProperties.SetName(target, AutomationProperties.GetName(source));
    }

    private static Color GetTileOutlineColor(bool selected) =>
        UiAccessibility.HighContrast ? UiAccessibility.SystemColor(selected ? 13 : 8) :
            selected ? GetSystemAccentColor() : Color.FromArgb(110, 170, 170, 170);

    private static void SetTileStateResources(Button button, SolidColorBrush outline)
    {
        // Keep a stable outline in every native state. Backgrounds start clear;
        // SettingsMotion supplies the subtle hover/pressed highlights separately.
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            button.Resources["ButtonBackground" + state] = TransparentBrush();
            button.Resources["ButtonBorderBrush" + state] = outline;
        }
    }

    internal static Style GetSettingsStyle(string key) => (Style)Application.Current.Resources[key];

    private void ConfigureWindow()
    {
        if (_fullWindow)
        {
            var fullPresenter = OverlappedPresenter.Create();
            fullPresenter.IsResizable = true;
            AppWindow.SetPresenter(fullPresenter);
            WindowHelpers.SetAppIcon(this);
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

        WindowHelpers.SetAppIcon(this);

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Owned + TOOLWINDOW means:
        // - no taskbar button
        // - no Alt+Tab entry
        // The menu accepts focus so its material follows real activation.
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
            RefreshContrastOutlines(_contentRoot);
        });
    }

#if LIQUID_GLASS
    internal void RefreshMaterialAppearance() => ApplyDwmAppearance(WinRT.Interop.WindowNative.GetWindowHandle(this));
#endif

    public void RefreshAppearance()
    {
        if (_closed) return;
        ApplySystemControlButtonTheme();
        RefreshContrastOutlines(_contentRoot);
        _updateScrollWidth?.Invoke();
        _transientScrollBar?.RefreshPreferences();
    }

    private static void RefreshContrastOutlines(DependencyObject? element)
    {
        if (element is null) return;
        if (element is Button { Tag: OptionTileState state })
        {
            state.Outline.Color = GetTileOutlineColor(state.Accent);
            SettingsMotion.RefreshHoverColors((Button)element);
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            RefreshContrastOutlines(VisualTreeHelper.GetChild(element, i));
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
#if LIQUID_GLASS
        UiOptions?.Dispose();
#endif
        _performanceChoices?.Dispose();
        _chargeChoicesUi?.Dispose();
        _transientScrollBar?.Dispose();
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

    private void ApplyDwmAppearance(nint hwnd)
    {
        // The material adapter owns the backdrop, not a competing DWM recipe.
        int preference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));

#if LIQUID_GLASS
        uint borderColor = LiquidGlassWindow.EffectsEnabled(this) ? DWMWA_COLOR_NONE :
            _fullWindow && !_isActive ? DWMWA_COLOR_DEFAULT : GetAccentColorRef();
#else
        // Force accent on the full window only while active, independently of
        // global accent settings. On deactivation restore DWM's default border.
        // Keep the compact tool window's existing accent behavior unchanged.
        uint borderColor = _fullWindow && !_isActive ? DWMWA_COLOR_DEFAULT : GetAccentColorRef();
#endif

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
    private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;

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
