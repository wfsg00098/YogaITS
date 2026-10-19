using System.Management;

namespace YogaITS.Tray.Services;

internal enum GpuWorkingMode { Default, IGPUOnly, Auto }

internal sealed record GpuWorkingSnapshot(
    bool Supported, GpuWorkingMode LogicalMode, int EffectiveMode, int ExternalPath,
    int PowerSource, bool NvidiaPresent, string Message)
{
    public static GpuWorkingSnapshot Unavailable { get; } = new(false, GpuWorkingMode.Default, -1, -1, -1, false, "");
}

internal sealed class LenovoGpuWorkingModeService : SerializedFeatureService
{
    private CancellationTokenSource? _retry;
    private GpuWorkingMode _logical = UserSettingsStore.Get("GpuWorkingMode", GpuWorkingMode.Default);
    private bool _hasPreference = UserSettingsStore.Get<int?>("GpuWorkingMode", null) is >= 0 and <= 2;
    public GpuWorkingSnapshot Snapshot { get; private set; } = GpuWorkingSnapshot.Unavailable;
    public bool AutoEventsAvailable { get; set; }
    public Func<bool, Task>? KeepAwakeModeChange { get; set; }

    private static ManagementObjectSearcher Search(string className) => new(
        new ManagementScope(@"\\.\root\WMI", new ConnectionOptions
        {
            EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate
        }), new ObjectQuery($"SELECT * FROM {className}"));

    private static int Get(string className, string method, string field)
    {
        using var search = Search(className);
        using var instances = search.Get();
        foreach (ManagementObject instance in instances)
        {
            using (instance)
            using (var result = instance.InvokeMethod(method, null, null))
                return Convert.ToInt32(result?[field] ?? throw new InvalidOperationException("WMI 返回为空。"));
        }
        throw new InvalidOperationException("未找到 Lenovo GPU WMI 实例。");
    }

    private static void Set(int mode)
    {
        using var search = Search("LENOVO_GAMEZONE_DATA");
        using var instances = search.Get();
        foreach (ManagementObject instance in instances)
        {
            using (instance)
            {
                using var input = instance.GetMethodParameters("SetIGPUModeStatus");
                input["mode"] = (uint)mode;
                using var result = instance.InvokeMethod("SetIGPUModeStatus", input, null);
                // Invoke on the enumerated instance; never access ReturnValue.
                return;
            }
        }
        throw new InvalidOperationException("未找到 Lenovo GPU WMI 实例。");
    }

    private void Read()
    {
        try
        {
            bool supported = Get("LENOVO_GAMEZONE_DATA", "IsSupportIGPUMode", "Data") > 0;
            if (!supported) { Snapshot = GpuWorkingSnapshot.Unavailable; return; }
            int effective = Get("LENOVO_GAMEZONE_DATA", "GetIGPUModeStatus", "Data");
            if (!_hasPreference)
                _logical = effective == 1 ? GpuWorkingMode.IGPUOnly : GpuWorkingMode.Default;
            int external;
            try { external = Get("LENOVO_IGPUMODE_HDMI_METHOD", "GetIGPUMHDMIStatus", "HDMI_Status"); }
            catch { external = -1; }
            Snapshot = new(true, _logical, effective, external, SystemEventMonitor.ReadPowerSource(),
                NvidiaGpuHelper.HasNvidiaDisplayDevice(),
                external > 0 ? "外接显示器正在使用独立显卡输出" : external < 0 ? "外接独显显示路径状态未知，暂不允许集显切换。" : "");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("GPU capability read failed: " + ex.Message);
            Snapshot = GpuWorkingSnapshot.Unavailable with { Message = ex.Message };
        }
    }

    public Task InitializeAsync(Func<bool> registerEvents) => RunAsync(async () =>
    {
        Read();
        if (Snapshot.Supported) AutoEventsAvailable = registerEvents();
        if (Snapshot.Supported && AutoEventsAvailable && _logical == GpuWorkingMode.Auto)
            await ApplyPolicyAsync(true);
    });

    public Task RefreshAsync() => RunAsync(() => { Read(); return Task.CompletedTask; });

    public Task SelectAsync(GpuWorkingMode mode)
    {
        CancelRetry();
        return RunAsync(async () =>
        {
            Read();
            if (!Snapshot.Supported) throw new InvalidOperationException("显卡工作模式当前不可用。");
            if (mode == GpuWorkingMode.Auto)
            {
                if (!AutoEventsAvailable) throw new InvalidOperationException("电源事件监听不可用，不能启用智能模式。");
                _logical = mode;
                UserSettingsStore.Set("GpuWorkingMode", mode);
                _hasPreference = true;
                await ApplyPolicyAsync(true);
                return;
            }
            int target = mode == GpuWorkingMode.IGPUOnly ? 1 : 0;
            if (target == 1 && Snapshot.ExternalPath != 0)
                throw new InvalidOperationException(Snapshot.Message);
            await SetAndReadAsync(target);
            if (Snapshot.EffectiveMode != target)
                throw new InvalidOperationException("切换命令已发送，但实际显卡状态尚未达到目标。");
            _logical = mode;
            UserSettingsStore.Set("GpuWorkingMode", mode);
            _hasPreference = true;
            Read();
        });
    }

    public Task HandleSystemChangeAsync()
    {
        // Cancel pending DC retries as soon as an AC/topology event arrives.
        CancelRetry();
        return RunAsync(async () => { Read(); await ApplyPolicyAsync(true); });
    }

    private async Task SetAndReadAsync(int target)
    {
        if (target == 1)
        {
            Read();
            if (!Snapshot.Supported || Snapshot.ExternalPath != 0)
                throw new InvalidOperationException("独显显示路径不可安全释放。");
        }
        if (target == 1 && KeepAwakeModeChange is { } before) await before(true);
        try
        {
            Set(target);
            await Task.Delay(1500).ConfigureAwait(false);
            Read();
        }
        finally
        {
            if (target == 1 && KeepAwakeModeChange is { } after) await after(false);
        }
    }

    private async Task ApplyPolicyAsync(bool allowRetry)
    {
        if (!Snapshot.Supported || !AutoEventsAvailable || _logical != GpuWorkingMode.Auto) return;
        try
        {
            int? target = GpuAutoPolicy.Target(Snapshot.PowerSource, Snapshot.ExternalPath);
            if (target == 0)
            {
                if (Snapshot.EffectiveMode != 0) await SetAndReadAsync(0);
            }
            else if (target == 1)
            {
                if (Snapshot.EffectiveMode != 1 || Snapshot.NvidiaPresent)
                    await SetAndReadAsync(1);
                if (allowRetry && Snapshot.Supported && Snapshot.PowerSource == 1 && (Snapshot.EffectiveMode != 1 || Snapshot.NvidiaPresent))
                    StartRetry();
            }
        }
        catch (Exception ex)
        {
            Snapshot = Snapshot with { Message = "智能模式切换未完成：" + ex.Message };
            if (allowRetry && Snapshot.PowerSource == 1 && Snapshot.ExternalPath == 0) StartRetry();
        }
        Snapshot = Snapshot with { LogicalMode = _logical };
    }

    private void StartRetry()
    {
        CancelRetry();
        var retry = new CancellationTokenSource();
        CancellationToken token = retry.Token;
        var previous = Interlocked.Exchange(ref _retry, retry);
        previous?.Cancel();
        previous?.Dispose();
        _ = RetryAsync(token);
    }

    private async Task RetryAsync(CancellationToken token)
    {
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(5000, token).ConfigureAwait(false);
                bool done = false;
                await RunAsync(async () =>
                {
                    if (token.IsCancellationRequested) { done = true; return; }
                    Read();
                    if (_logical != GpuWorkingMode.Auto || Snapshot.PowerSource != 1 || Snapshot.ExternalPath != 0)
                    { done = true; return; }
                    await ApplyPolicyAsync(false);
                    done = Snapshot.EffectiveMode == 1 && !Snapshot.NvidiaPresent;
                });
                if (done) return;
            }
        }
        catch (OperationCanceledException) { }
    }

    public void CancelRetry()
    {
        var retry = Interlocked.Exchange(ref _retry, null);
        retry?.Cancel();
        retry?.Dispose();
    }

    protected override void Close() => CancelRetry();
}
