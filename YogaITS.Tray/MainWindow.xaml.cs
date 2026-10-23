using Microsoft.UI.Xaml;
using YogaITS.Tray.Services;
using YogaITS.Tray.UI;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.Services.Updates;
using YogaITS.Tray.Services.KeyboardBacklight;

namespace YogaITS.Tray;

public sealed partial class MainWindow : Window
{
    private readonly List<Window> _childWindows = [];
    private readonly AppStateCache _stateCache;

    private NativeTrayIcon? _trayIcon;
    private TrayMenuWindow? _trayMenuWindow;
    private TrayMenuWindow? _controlCenterWindow;
    private readonly LenovoGpuWorkingModeService _gpu = new();
    private readonly HpdService _hpd = new();
    private readonly ColorGamutService _color = new();
    private readonly ElevocService _audio = new();
    private readonly KeyboardBacklightService _keyboardBacklight = new();
    private readonly GpuKeepAwakeService _keepAwake = new();
    private bool _gpuInitializationComplete;
    private readonly AppUpdateService _updates = new();
    private bool _updateRequested;
    private SystemEventMonitor? _systemEvents;
    private FeaturePanels? _featurePanels;
    private readonly AsyncOperationQueue _coreRefreshQueue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SerializedActionService _itsActions = new(), _chargeActions = new(), _flipActions = new(), _startupActions = new();
    private bool _exiting;
    private bool _controlCenterRefreshing;
    private CancellationTokenSource? _colorRefreshDebounce;

    private nint _mainHwnd;

    public MainWindow()
    {
        InitializeComponent();

        // Establish the hidden message host and tray before background probes.
        // Opening a window always uses a cache, never synchronous hardware I/O.
        _stateCache = AppStateCache.Initialize();
        _stateCache.Changed += () => DispatcherQueue.TryEnqueue(ApplyCoreState);
        foreach (var actions in new[] { _itsActions, _chargeActions, _flipActions, _startupActions })
            actions.Changed += () => DispatcherQueue.TryEnqueue(ApplyCoreState);
        _keepAwake.Changed += () => DispatcherQueue.TryEnqueue(RefreshKeepAwakeCards);
        _gpu.SuspendKeepAwake = () => _keepAwake.SuspendForGpuModeAsync(_gpu.StateRevision);
        _gpu.CompleteKeepAwake = actual => _keepAwake.CompleteGpuModeAsync(
            actual == 0 ? NvidiaGpuHelper.HasNvidiaDisplayDevice() : _stateCache.Snapshot.NvidiaGpuSupported, actual, _gpu.StateRevision);
        _gpu.KeepAwakeDiagnostic = () => $"Enabled={_keepAwake.Snapshot.Enabled}; Active={_keepAwake.Snapshot.Active}";

        Closed += MainWindow_Closed;
        InitializeHiddenTrayHost();
        try { _systemEvents = new SystemEventMonitor(_mainHwnd); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("System event registration failed: " + ex.Message);
            ShowChild(new InfoWindow("系统事件监听不可用", "无法监听电源及显示变化，智能显卡模式不可用。请重新启动应用。"));
        }
        if (_systemEvents is not null)
        {
            // Color management needs AC/DC events even on machines without GPU modes.
            _systemEvents.EnablePowerNotifications();
            _systemEvents.PowerChanged += () => _ = RefreshKeepAwakeForSystemChangeAsync();
            _systemEvents.DisplayChanged += () => _ = RefreshKeepAwakeForSystemChangeAsync();
            _systemEvents.SleepStateChanged += suspended => _ = RefreshKeepAwakeForSystemChangeAsync(suspended);
            _systemEvents.DisplayPowerChanged += displayOn => _ = RefreshKeepAwakeForSystemChangeAsync(displayOn: displayOn);
            _systemEvents.EnableDisplayPowerNotifications();
            _systemEvents.PowerChanged += () => _ = RefreshGpuForSystemChangeAsync("电源事件");
            _systemEvents.DisplayChanged += () => _ = RefreshGpuForSystemChangeAsync("屏幕事件");
            _systemEvents.PowerChanged += () => DispatcherQueue.TryEnqueue(ScheduleColorRefresh);
            _systemEvents.PowerChanged += () => DispatcherQueue.TryEnqueue(() =>
            {
                _trayMenuWindow?.RefreshPowerSource();
                _controlCenterWindow?.RefreshPowerSource();
            });
            _systemEvents.DisplayChanged += () => DispatcherQueue.TryEnqueue(ScheduleColorRefresh);
            _systemEvents.AppearanceChanged += () => DispatcherQueue.TryEnqueue(() =>
            {
                _trayMenuWindow?.RefreshAppearance();
                _controlCenterWindow?.RefreshAppearance();
            });
        }
        _gpu.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_exiting) return;
            if (_gpu.Snapshot.Supported && _gpu.Snapshot.Available)
            {
                _gpu.AutoEventsAvailable = _systemEvents?.EnablePowerNotifications() ?? false;
                // Publish the completed GPU read directly, including Auto retries.
                // Never defer this behind window activation or a full-window refresh.
                _stateCache.UpdateNvidiaPresence(_gpu.Snapshot.NvidiaPresent);
            }
            _featurePanels?.Render();
            // The mode may change before PnP removes the NVIDIA device. Update
            // visibility even when UpdateNvidiaPresence did not raise Changed.
            ApplyCoreState();
        });
        _ = InitializeGpuAndKeepAwakeAsync();
        _hpd.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _hpd.RefreshAsync();
        _color.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _color.RefreshAsync();
        _audio.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _audio.RefreshAsync();
        _keyboardBacklight.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _keyboardBacklight.RefreshAsync();
        _updates.Changed += () => DispatcherQueue.TryEnqueue(RefreshUpdateCard);
        _ = InitializeCoreAsync();
    }

    private void InitializeHiddenTrayHost()
    {
        _mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppRuntimeSnapshot state = _stateCache.Snapshot;

        _trayIcon = new NativeTrayIcon(
            _mainHwnd,
            BuildTooltip(state));

        _trayIcon.LeftClick += () => DispatcherQueue.TryEnqueue(ToggleControlCenter);
        _trayIcon.RightClick += TrayIcon_Click;

        // Keep the HWND/message pump alive, but never present this host
        // window to the user. Because App.OnLaunched no longer calls Activate(),
        // this window is never shown even for a single frame.
        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch
        {
        }

        AppWindow.Hide();
        _ = ShowWindow(_mainHwnd, SW_HIDE);
    }

    private void TrayIcon_Click()
    {
        DispatcherQueue.TryEnqueue(ShowTrayMenu);
    }

    private void ShowTrayMenu()
        => ShowSettingsWindow(false);

    private void ShowControlCenter()
    {
        if (_controlCenterWindow is not null)
        {
            _controlCenterWindow.ShowInForeground();
            return;
        }
        ShowSettingsWindow(true);
    }

    private void ToggleControlCenter()
    {
        if (_exiting) return;
        if (_controlCenterWindow is { } window)
            window.Close(); // Closed releases panels and clears the window reference.
        else
            ShowControlCenter();
    }

    internal void RequestOpenControlCenter() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_exiting) ShowControlCenter();
    });

    private void ShowSettingsWindow(bool fullWindow)
    {
        // Critical path: cache only. No WMI, EnergyDrv, registry/service state
        // probing, or schtasks query is performed before the window is shown.
        AppRuntimeSnapshot state = GetWindowState();
        _trayIcon?.SetTooltip(BuildTooltip(state));

        try
        {
            try
            {
                if (!fullWindow)
                    _trayMenuWindow?.Close();
            }
            catch
            {
            }

            var menu = new TrayMenuWindow(
                ownerHwnd: _mainHwnd,
                modelName: state.MachineInfo.DisplayModel,
                itsCapabilities: state.ItsCapabilities,
                snapshot: state.ItsMode,
                batterySnapshot: state.BatteryCharge,
                nightChargeSnapshot: state.NightCharge,
                flipToStartSnapshot: state.FlipToStart,
                nvidiaGpuSupported: state.NvidiaGpuSupported,
                startupEnabled: state.StartupEnabled,
                setMode: SetModeAsync,
                setChargeOption: SetChargeOptionAsync,
                setFlipToStart: SetFlipToStartAsync,
                killGpuApps: KillGpuAppsDirectAsync,
                restartGpu: RestartGpuDirectAsync,
                setStartup: SetStartupAsync,
                showInfo: (title, message) =>
                    ShowChild(new InfoWindow(title, message)),
                exitApp: ExitApplication,
                fullWindow: fullWindow,
                keepAwake: _keepAwake.Snapshot,
                setKeepAwake: _keepAwake.SetEnabledAsync);
            menu.ApplyRefreshedState(state);

            if (fullWindow)
            {
                _controlCenterWindow = menu;
                RefreshUpdateCard();
                _featurePanels = new FeaturePanels(menu.AdditionalSections, _gpu, _hpd, _color, _audio, _keyboardBacklight);
                bool active = false;
                menu.Activated += (_, args) =>
                {
                    bool nowActive = args.WindowActivationState != WindowActivationState.Deactivated;
                    if (nowActive && !active && ReferenceEquals(_controlCenterWindow, menu))
                        _ = RefreshControlCenterAsync();
                    active = nowActive;
                };
            }
            else
                _trayMenuWindow = menu;

            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_trayMenuWindow, menu))
                    _trayMenuWindow = null;
                if (ReferenceEquals(_controlCenterWindow, menu))
                {
                    _controlCenterWindow = null;
                    _featurePanels?.Dispose();
                    _featurePanels = null;
                    _audio.CancelRegistration();
                }
            };

            if (fullWindow)
                menu.ShowControlCenter();
            else
                menu.ShowAtCursor();

            // Once the cached UI is already visible, read the dynamic states as
            // one background refresh batch. When complete, apply them to the
            // still-open menu in place.
            if (!fullWindow) _ = RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            // Also cover TrayMenuWindow construction itself. A visual/control
            // creation failure must not terminate the resident tray process.
            ShowChild(new InfoWindow("托盘菜单显示失败", ex.ToString()));
        }
    }

    private async Task RefreshControlCenterAsync()
    {
        if (_exiting || _controlCenterRefreshing) return;
        _controlCenterRefreshing = true;
        try
        {
            await Task.WhenAll(RefreshCoreAsync(), _gpu.RefreshAsync(), _hpd.RefreshAsync(),
                _color.RefreshAsync(), _audio.RefreshAsync(), _keyboardBacklight.RefreshAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Control center refresh failed: " + ex.GetType().Name);
        }
        finally { _controlCenterRefreshing = false; }
    }

    private void ScheduleColorRefresh()
    {
        if (_exiting) return;
        _colorRefreshDebounce?.Cancel();
        _colorRefreshDebounce?.Dispose();
        _colorRefreshDebounce = new CancellationTokenSource();
        _ = RefreshColorForSystemChangeAsync(_colorRefreshDebounce.Token);
    }

    private async Task RefreshColorForSystemChangeAsync(CancellationToken token)
    {
        try
        {
            // Windows applies battery HDR policy asynchronously. Two bounded,
            // coalesced reads cover settling without introducing periodic polling.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await Task.Delay(750, token);
                if (_exiting || token.IsCancellationRequested) return;
                await _color.RefreshAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Color event refresh failed: " + ex.Message);
        }
    }

    private async Task RefreshKeepAwakeForSystemChangeAsync(bool? suspended = null, bool? displayOn = null)
    {
        if (_exiting) return;
        try
        {
            if (suspended is { } sleeping) await _keepAwake.SetSuspendedAsync(sleeping);
            else if (displayOn is { } on) await _keepAwake.SetDisplayOnAsync(on);
            else await _keepAwake.RefreshEnvironmentAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Keep-awake event handling failed: " + ex.GetType().Name);
        }
    }

    private async Task RefreshGpuForSystemChangeAsync(string trigger)
    {
        if (_exiting) return;
        try
        {
            System.Diagnostics.Debug.WriteLine("GPU 后台刷新：" + trigger);
            // The hidden message host owns these subscriptions for the app lifetime.
            // The service reads in every mode; only Auto is allowed to apply policy.
            await _gpu.HandleSystemChangeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("GPU 事件处理失败：" + ex.Message);
        }
        finally
        {
            // Refresh both menus even when a focus-triggered batch is in flight.
            if (!_exiting) await RefreshCoreAsync();
        }
    }

    private AppRuntimeSnapshot GetWindowState()
    {
        var state = _stateCache.Snapshot;
        var mode = _gpu.Snapshot;
        // UI-only projection: leave actual presence and the persisted keep-awake
        // preference untouched so a paused preference can resume later.
        return state with { NvidiaGpuSupported = GpuKeepAwakePolicy.ShowGpuControls(
            state.NvidiaGpuSupported, mode.Supported, mode.EffectiveMode),
            ItsAvailable = state.ItsAvailable && _itsActions.IsResponsive,
            BatteryAvailable = state.BatteryAvailable && _chargeActions.IsResponsive,
            NightAvailable = state.NightAvailable && _chargeActions.IsResponsive,
            FlipAvailable = state.FlipAvailable && _flipActions.IsResponsive,
            StartupAvailable = state.StartupAvailable && _startupActions.IsResponsive };
    }

    private void ApplyCoreState()
    {
        if (_exiting) return;
        AppRuntimeSnapshot state = GetWindowState();
        _trayIcon?.SetTooltip(BuildTooltip(state));
        _trayMenuWindow?.ApplyRefreshedState(state);
        _controlCenterWindow?.ApplyRefreshedState(state);
        RefreshKeepAwakeCards();
        _ = ConfigureKeepAwakeAsync();
        RefreshUpdateCard();
    }

    private async Task InitializeGpuAndKeepAwakeAsync()
    {
        try { await _gpu.InitializeAsync(() => _systemEvents?.EnablePowerNotifications() ?? false); }
        finally { _gpuInitializationComplete = true; await ConfigureKeepAwakeAsync(); }
    }

    private async Task ConfigureKeepAwakeAsync()
    {
        if (_exiting || !_gpuInitializationComplete) return;
        try
        {
            var mode = _gpu.Snapshot;
            await _keepAwake.ConfigureAsync(_stateCache.Snapshot.NvidiaGpuSupported,
                mode with { Available = mode.Available && _gpu.IsResponsive });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Keep-awake setup failed: " + ex.GetType().Name); }
    }

    private void RefreshKeepAwakeCards()
    {
        if (_exiting) return;
        _trayMenuWindow?.ApplyKeepAwakeState(_keepAwake.Snapshot);
        _controlCenterWindow?.ApplyKeepAwakeState(_keepAwake.Snapshot);
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            foreach (int delay in new[] { 0, 5000, 15000 })
            {
                await Task.Delay(delay, _lifetime.Token);
                if (_exiting) return;
                await RefreshCoreAsync();
                if (_stateCache.Snapshot.ItsCapabilities.Supported) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task RefreshCoreAsync() => _coreRefreshQueue.Enqueue(async () =>
    {
        try
        {
            if (_exiting) return;
            await _stateCache.RefreshDynamicAsync().ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(ApplyCoreState);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Core refresh: " + ex.GetType().Name); }
    }, refresh: true);

    private static string BuildTooltip(AppRuntimeSnapshot state)
    {
        return "Yoga控制中心";
    }

    private async Task<ItsSetResult> SetModeAsync(ItsMode mode)
    {
        try
        {
            ItsCapabilitySnapshot capabilities =
                _stateCache.Snapshot.ItsCapabilities;

            ItsSetResult result =
                await _itsActions.ExecuteAsync(async () =>
                {
                    var actual = await LenovoItsHelper.SetModeAsync(mode, capabilities);
                    _stateCache.UpdateItsMode(actual.Snapshot);
                    return actual;
                });
            _trayIcon?.SetTooltip(BuildTooltip(_stateCache.Snapshot));

            if (!result.Verified)
            {
                ShowChild(new InfoWindow(
                    "Yoga ITS",
                    $"切换命令已发送，但注册表当前显示：{result.Snapshot.DisplayName}。\n\n" +
                    "程序不会直接写 ITS 状态注册表；这里只用它确认联想性能服务的回读结果。"));
            }

            return result;
        }
        catch (Exception ex)
        {
            ShowChild(new InfoWindow(
                "切换失败",
                ex.Message));

            return new ItsSetResult(
                _stateCache.Snapshot.ItsMode,
                Verified: false);
        }
    }

    private Task<BatterySelectionResult> SetChargeOptionAsync(BatteryChargeMode mode, bool night) =>
        _chargeActions.ExecuteAsync(async () =>
        {
            var result = await BatteryChargeSelection.ApplyAsync(mode, night,
                LenovoBatteryHelper.ReadNightCharge, LenovoBatteryHelper.ReadChargeMode,
                LenovoBatteryHelper.SetNightChargeAsync, LenovoBatteryHelper.SetChargeModeAsync);
            _stateCache.UpdateBatterySelection(result.Charge, result.Night);
            return result;
        });

    private Task<FlipToStartResult> SetFlipToStartAsync(bool enabled) => _flipActions.ExecuteAsync(async () =>
    {
        FlipToStartResult result =
            await LenovoFlipToStartHelper.SetAsync(enabled);

        _stateCache.UpdateFlipToStart(result.Snapshot);
        return result;
    });

    private Task<StartupTaskResult> SetStartupAsync(bool enabled) => _startupActions.ExecuteAsync(async () =>
    {
        StartupTaskResult result =
            await StartupTaskHelper.SetEnabledAsync(enabled);

        if (result.Enabled is bool actual)
            _stateCache.UpdateStartup(actual);

        return result;
    });

    private async Task KillGpuAppsDirectAsync()
    {
        try
        {
            IReadOnlyList<GpuProcessInfo> all =
                NvidiaGpuHelper.QueryActiveApplications();

            IReadOnlyList<GpuProcessInfo> candidates =
                NvidiaGpuHelper.GetKillCandidates(all);

            if (candidates.Count == 0)
                return;

            GpuActionResult result =
                await NvidiaGpuHelper.KillProcessesAsync(
                    candidates);

            if (!result.Success)
            {
                ShowChild(new InfoWindow(
                    "独显休眠处理未完全成功",
                    result.Message));
            }
        }
        catch (Exception ex)
        {
            ShowChild(new InfoWindow(
                "结束独显占用进程失败",
                ex.Message));
        }
    }

    private async Task RestartGpuDirectAsync()
    {
        if (_keepAwake.Snapshot.Enabled) return;
        try
        {
            GpuActionResult result =
                await NvidiaGpuHelper.RestartNvidiaDisplayDeviceAsync();

            if (!result.Success)
            {
                ShowChild(new InfoWindow(
                    "重启独显设备失败",
                    result.Message));
            }
        }
        catch (Exception ex)
        {
            ShowChild(new InfoWindow(
                "重启独显设备失败",
                ex.Message));
        }
    }

    private void ShowChild(Window window)
    {
        _childWindows.Add(window);
        window.Closed += (_, _) =>
            _childWindows.Remove(window);
        window.Activate();
    }

    private void RefreshUpdateCard()
    {
        if (_exiting || _controlCenterWindow is not { } menu) return;
        menu.SetAvailableUpdate(_updates.Available, _updates.Busy, () => InstallUpdateAsync(menu));
    }

    private async Task InstallUpdateAsync(TrayMenuWindow menu)
    {
        if (_updateRequested || _exiting || _updates.Available is not { } release) return;
        _updateRequested = true;
        try
        {
            if (!await menu.ConfirmUpdateAsync(release.Version)) return;
            await _updates.StartInstallerAsync(release);
            await ExitApplicationAsync();
        }
        catch (Exception ex)
        {
            if (!_exiting) ShowChild(new InfoWindow("更新失败", ex.Message + "\n现有程序未被替换，可以稍后重试。"));
        }
        finally { _updateRequested = false; }
    }

    private void ExitApplication()
        => _ = ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _lifetime.Cancel();
        _colorRefreshDebounce?.Cancel();
        _colorRefreshDebounce?.Dispose();
        _gpu.CancelRetry();
        _audio.CancelRegistration();
        _color.CancelPendingOperations();
        _updates.Dispose();
        _systemEvents?.Dispose();
        _systemEvents = null;
        try
        {
            await Task.WhenAll(_keepAwake.DisposeAsync().AsTask(), _gpu.DisposeAsync().AsTask(), _hpd.DisposeAsync().AsTask(),
                _color.DisposeAsync().AsTask(), _audio.DisposeAsync().AsTask(), _keyboardBacklight.DisposeAsync().AsTask(), _itsActions.DisposeAsync().AsTask(),
                _chargeActions.DisposeAsync().AsTask(), _flipActions.DisposeAsync().AsTask(),
                _startupActions.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Feature cleanup: " + ex.Message); }
        DisposeTray();
        Close();
        Environment.Exit(0);
    }

    private void MainWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        DisposeTray();
    }

    private void DisposeTray()
    {
        try
        {
            _trayMenuWindow?.Close();
            _controlCenterWindow?.Close();
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
        catch
        {
        }
    }

    private const int SW_HIDE = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(
        nint hWnd,
        int nCmdShow);
}
