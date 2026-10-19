using System.Diagnostics;

namespace YogaITS.Tray.Services;

internal sealed class GpuKeepAwakeService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _monitor;
    private Process? _process;
    private EventWaitHandle? _stop;
    private EventWaitHandle? _check;
    private TaskCompletionSource<bool>? _healthReply;
    private bool _disposed, _present, _modeBlocked, _switching, _active, _configured;
    private bool _suspended;
    private int _powerSource = -1;
    private bool _enabled = UserSettingsStore.Get("KeepDGpuAwake", false);
    private string _message = "";
    public event Action? Changed;
    public GpuKeepAwakeSnapshot Snapshot { get; private set; }

    public GpuKeepAwakeService()
    {
        Snapshot = new(_enabled, false, "");
        _monitor = MonitorAsync();
    }

    private Task RunAsync(Func<Task> operation) => Task.Run(async () =>
    {
        await _gate.WaitAsync();
        try { if (!_disposed) await operation(); }
        finally { try { Publish(); } finally { _gate.Release(); } }
    });

    private void Publish()
    {
        var next = new GpuKeepAwakeSnapshot(_enabled, _active, _message);
        if (next == Snapshot) return;
        Snapshot = next;
        Changed?.Invoke();
    }

    public Task ConfigureAsync(bool present, GpuWorkingSnapshot mode, bool endSwitch = false) => RunAsync(async () =>
    {
        bool blocked = mode.Supported && mode.EffectiveMode == 1;
        if (_configured && _present == present && _modeBlocked == blocked &&
            _powerSource == SystemEventMonitor.ReadPowerSource() && !endSwitch) return;
        _configured = true;
        _present = present;
        _modeBlocked = blocked;
        if (endSwitch) _switching = false;
        await ReconcileAsync();
    });

    // Independent of WMI refreshes: disconnecting AC must not wait for GPU queries.
    public Task RefreshEnvironmentAsync() => RunAsync(() => ReconcileAsync(checkHealth: true));

    public Task SetSuspendedAsync(bool suspended) => RunAsync(async () =>
    {
        _suspended = suspended;
        await ReconcileAsync(checkHealth: !suspended);
    });

    public Task PauseForModeSwitchAsync() => RunAsync(async () =>
    {
        _switching = true;
        await StopAsync();
    });

    public async Task<GpuKeepAwakeSnapshot> SetEnabledAsync(bool enabled)
    {
        await RunAsync(async () =>
        {
            bool previous = _enabled;
            try
            {
                if (enabled && !_present) throw new InvalidOperationException("当前没有可用的 NVIDIA 独显。");
                _enabled = enabled;
                await ReconcileAsync();
                UserSettingsStore.Set("KeepDGpuAwake", enabled);
            }
            catch
            {
                _enabled = previous;
                await ReconcileAsync();
                throw;
            }
        });
        return Snapshot;
    }

    private async Task ReconcileAsync(bool checkHealth = false)
    {
        _powerSource = SystemEventMonitor.ReadPowerSource();
        if (!GpuKeepAwakePolicy.MayRun(_enabled, _present, true, _modeBlocked ? 1 : 0,
            _powerSource, _suspended) || _switching)
        {
            await StopAsync();
            _message = !_enabled ? "" : _powerSource != 0
                ? "未连接交流电源，已暂停保持唤醒；接通电源后将自动恢复。"
                : "当前暂不保持唤醒，条件恢复后将自动恢复。";
            return;
        }
        if (_process is { HasExited: false } && (!checkHealth || await CheckHealthAsync())) return;
        await StopAsync();
        string helper = Path.Combine(AppContext.BaseDirectory, GpuKeepAwakePolicy.HelperFileName);
        if (!File.Exists(helper))
        {
            _message = "缺少保持独显唤醒助手，请使用完整发布目录。";
            throw new FileNotFoundException(_message, helper);
        }
        string name = @"Local\YogaITS.dGPUKeepAwake.Stop." + Guid.NewGuid().ToString("N");
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        string checkName = @"Local\YogaITS.dGPUKeepAwake.Check." + Guid.NewGuid().ToString("N");
        _check = new EventWaitHandle(false, EventResetMode.AutoReset, checkName);
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(checkName);
        try { _process = Process.Start(start) ?? throw new IOException("无法启动保持独显唤醒助手。"); }
        catch { _stop.Dispose(); _stop = null; _check.Dispose(); _check = null; throw; }
        _message = "正在建立 NVIDIA 独显空闲上下文。";
        _ = ReadStatusAsync(_process);
    }

    private async Task<bool> CheckHealthAsync()
    {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _healthReply, reply);
        try
        {
            if (_check is null) return false;
            _check.Set();
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
        }
        catch (TimeoutException) { return false; }
        finally { Volatile.Write(ref _healthReply, null); }
    }

    private async Task ReadStatusAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(_lifetime.Token) is { } status)
            {
                if (status is not ("ACTIVE" or "INVALID")) continue;
                // The reconciliation awaiting this reply owns _gate. Signal
                // before queuing a UI state update under that same gate.
                if (ReferenceEquals(_process, process))
                    Volatile.Read(ref _healthReply)?.TrySetResult(status == "ACTIVE");
                _ = RunAsync(() =>
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _active = status == "ACTIVE";
                        _message = _active ? "" : "NVIDIA 独显上下文已失效，等待主程序恢复。";
                    }
                    return Task.CompletedTask;
                });
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        if (ReferenceEquals(_process, process)) Volatile.Read(ref _healthReply)?.TrySetResult(false);
        await RunAsync(() =>
        {
            if (ReferenceEquals(_process, process)) { _active = false; _message = "保持唤醒助手已退出，等待恢复。"; }
            return Task.CompletedTask;
        });
    }

    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(GpuKeepAwakePolicy.CheckInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                try { await RunAsync(() => ReconcileAsync(checkHealth: true)); }
                catch (Exception ex) { Debug.WriteLine("Keep-awake recovery failed: " + ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task StopAsync()
    {
        Process? process = _process;
        _process = null;
        _active = false;
        try
        {
            _stop?.Set();
            if (process is { HasExited: false })
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    process.Kill(); // Only the exact helper process created by us.
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        }
        finally
        {
            process?.Dispose();
            _stop?.Dispose(); _stop = null;
            _check?.Dispose(); _check = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try { _disposed = true; await StopAsync(); }
        finally { _gate.Release(); }
        await _monitor;
        // Status reads use the canceled token and may still finish their final
        // queued update; retain the small synchronization objects until GC.
    }
}
