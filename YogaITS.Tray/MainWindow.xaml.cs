using Microsoft.UI.Xaml;
using YogaITS.Tray.Services;
using YogaITS.Tray.UI;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;

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
    private SystemEventMonitor? _systemEvents;
    private FeaturePanels? _featurePanels;
    private readonly SemaphoreSlim _coreRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _chargeSelectionGate = new(1, 1);
    private bool _exiting;
    private bool _controlCenterRefreshing;

    private nint _mainHwnd;
    private DateTime _lastTrayHoverRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        // Do all capability discovery and the first state read at application
        // startup. This hidden host is never shown, so menu opening later is
        // independent of WMI / EnergyDrv / schtasks latency.
        _stateCache = AppStateCache.Initialize();
        _stateCache.Changed += () => DispatcherQueue.TryEnqueue(ApplyCoreState);

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
            _systemEvents.PowerChanged += () => _ = _gpu.HandleSystemChangeAsync();
            _systemEvents.DisplayChanged += () => _ = _gpu.HandleSystemChangeAsync();
            _systemEvents.DisplayChanged += () => _ = _color.RefreshAsync();
        }
        _gpu.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_gpu.Snapshot.Supported) _gpu.AutoEventsAvailable = _systemEvents?.EnablePowerNotifications() ?? false;
            _featurePanels?.Render();
            if (!_controlCenterRefreshing) _ = RefreshCoreAsync();
        });
        _ = _gpu.InitializeAsync(() => _systemEvents?.EnablePowerNotifications() ?? false);
        _hpd.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _hpd.RefreshAsync();
        _color.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _color.RefreshAsync();
        _audio.Changed += () => DispatcherQueue.TryEnqueue(() => _featurePanels?.Render());
        _ = _audio.RefreshAsync();
    }

    private void InitializeHiddenTrayHost()
    {
        string iconPath =
            Path.Combine(AppContext.BaseDirectory, "Assets", "YogaITS.ico");

        _mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppRuntimeSnapshot state = _stateCache.Snapshot;

        _trayIcon = new NativeTrayIcon(
            _mainHwnd,
            iconPath,
            BuildTooltip(state));

        _trayIcon.MouseMove += TrayIcon_MouseMove;
        _trayIcon.LeftClick += () => DispatcherQueue.TryEnqueue(ShowControlCenter);
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

    private void TrayIcon_MouseMove()
    {
        // Tooltip response is cache-only. A throttled background read then
        // refreshes the cached ITS state for the next hover/menu open.
        _trayIcon?.SetTooltip(BuildTooltip(_stateCache.Snapshot));

        DateTime now = DateTime.UtcNow;
        if ((now - _lastTrayHoverRefresh).TotalMilliseconds < 750)
            return;

        _lastTrayHoverRefresh = now;
        _ = RefreshItsTooltipInBackgroundAsync();
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

    private void ShowSettingsWindow(bool fullWindow)
    {
        // Critical path: cache only. No WMI, EnergyDrv, registry/service state
        // probing, or schtasks query is performed before the window is shown.
        AppRuntimeSnapshot state = _stateCache.Snapshot;
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
                killGpuApps: KillGpuAppsDirect,
                restartGpu: RestartGpuDirect,
                setStartup: SetStartupAsync,
                showInfo: (title, message) =>
                    ShowChild(new InfoWindow(title, message)),
                exitApp: ExitApplication,
                fullWindow: fullWindow);

            if (fullWindow)
            {
                _controlCenterWindow = menu;
                _featurePanels = new FeaturePanels(menu.AdditionalSections, _gpu, _hpd, _color, _audio);
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
                _color.RefreshAsync(), _audio.RefreshAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Control center refresh failed: " + ex.GetType().Name);
        }
        finally { _controlCenterRefreshing = false; }
    }

    private void ApplyCoreState()
    {
        AppRuntimeSnapshot state = _stateCache.Snapshot;
        _trayIcon?.SetTooltip(BuildTooltip(state));
        _trayMenuWindow?.ApplyRefreshedState(state);
        _controlCenterWindow?.ApplyRefreshedState(state);
    }

    private async Task RefreshCoreAsync()
    {
        await _coreRefreshGate.WaitAsync();
        try
        {
            if (_exiting) return;
            await _stateCache.RefreshDynamicAsync().ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(ApplyCoreState);
        }
        catch
        {
            // Background refresh is best-effort. The already-visible cached UI
            // remains valid and responsive if one of the probes fails.
        }
        finally { _coreRefreshGate.Release(); }
    }

    private async Task RefreshItsTooltipInBackgroundAsync()
    {
        try
        {
            AppRuntimeSnapshot refreshed =
                await _stateCache.RefreshItsModeAsync().ConfigureAwait(false);

            DispatcherQueue.TryEnqueue(() =>
                _trayIcon?.SetTooltip(BuildTooltip(refreshed)));
        }
        catch
        {
        }
    }

    private static string BuildTooltip(AppRuntimeSnapshot state)
    {
        return state.ItsCapabilities.Supported
            ? $"Yoga ITS - {state.ItsMode.DisplayName}"
            : $"Yoga ITS - {state.MachineInfo.DisplayModel}";
    }

    private async Task<ItsSetResult> SetModeAsync(ItsMode mode)
    {
        try
        {
            ItsCapabilitySnapshot capabilities =
                _stateCache.Snapshot.ItsCapabilities;

            ItsSetResult result =
                await LenovoItsHelper.SetModeAsync(mode, capabilities);

            _stateCache.UpdateItsMode(result.Snapshot);
            _trayIcon?.SetTooltip(BuildTooltip(_stateCache.Snapshot));

            if (!result.Verified)
            {
                ShowChild(new InfoWindow(
                    "Yoga ITS",
                    $"切换命令已发送，但注册表当前显示：{result.Snapshot.DisplayName}。\n\n" +
                    "程序不会直接写 ITS 状态注册表；这里只用它确认 LenovoProcessManagement 的实际结果。"));
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

    private async Task<BatterySelectionResult> SetChargeOptionAsync(BatteryChargeMode mode, bool night)
    {
        await _chargeSelectionGate.WaitAsync();
        try
        {
            var result = await Task.Run(() => BatteryChargeSelection.ApplyAsync(mode, night,
                LenovoBatteryHelper.ReadNightCharge, LenovoBatteryHelper.ReadChargeMode,
                LenovoBatteryHelper.SetNightChargeAsync, LenovoBatteryHelper.SetChargeModeAsync));
            _stateCache.UpdateBatterySelection(result.Charge, result.Night);
            return result;
        }
        finally { _chargeSelectionGate.Release(); }
    }

    private async Task<FlipToStartResult> SetFlipToStartAsync(bool enabled)
    {
        FlipToStartResult result =
            await LenovoFlipToStartHelper.SetAsync(enabled);

        _stateCache.UpdateFlipToStart(result.Snapshot);
        return result;
    }

    private async Task<StartupTaskResult> SetStartupAsync(bool enabled)
    {
        StartupTaskResult result =
            await StartupTaskHelper.SetEnabledAsync(enabled);

        if (result.Success)
            _stateCache.UpdateStartup(enabled);

        return result;
    }

    private void KillGpuAppsDirect()
    {
        _ = KillGpuAppsDirectAsync();
    }

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
                    candidates.Select(p => p.Pid));

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

    private void RestartGpuDirect()
    {
        _ = RestartGpuDirectAsync();
    }

    private async Task RestartGpuDirectAsync()
    {
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

    private void ExitApplication()
        => _ = ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _gpu.CancelRetry();
        _audio.CancelRegistration();
        _systemEvents?.Dispose();
        _systemEvents = null;
        try
        {
            await Task.WhenAll(_gpu.DisposeAsync().AsTask(), _hpd.DisposeAsync().AsTask(),
                _color.DisposeAsync().AsTask(), _audio.DisposeAsync().AsTask());
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
