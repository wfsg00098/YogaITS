namespace YogaITS.Tray.Services;

internal enum GpuWorkingMode { Default, IGPUOnly, Auto }

internal sealed record GpuWorkingSnapshot(
    bool Supported, GpuWorkingMode LogicalMode, int EffectiveMode, DgpuDisplayState DgpuDisplayState,
    int PowerSource, bool NvidiaPresent, string Message, bool Available = true, long Revision = 0)
{
    public static GpuWorkingSnapshot Unavailable { get; } = new(false, GpuWorkingMode.Default, -1,
        DgpuDisplayState.Unknown, -1, false, "", false);
}

internal sealed class LenovoGpuWorkingModeService : SerializedFeatureService
{
    private readonly IGpuModeBackend _backend;
    private readonly Action<GpuWorkingMode> _savePreference;
    private readonly Func<Task>? _settle;
    private readonly TimeSpan _retryDelay;
    private CancellationTokenSource? _retry;
    private GpuWorkingMode _logical;
    private bool _hasPreference;
    private long _capability;
    private long _revision;
    public long StateRevision => Interlocked.Read(ref _revision);
    public GpuWorkingSnapshot Snapshot { get; private set; } = GpuWorkingSnapshot.Unavailable;
    public GpuModeOperationResult? LastOperation { get; private set; }
    public string ActiveDiagnostic { get; private set; } = "";
    public string CapabilityDiagnostic { get; private set; } = "Not probed";
    public DgpuDisplayQueryResult DisplayQuery { get; private set; } = new(DgpuDisplayState.Unknown);
    public bool AutoEventsAvailable { get; set; }
    public Func<Task>? SuspendKeepAwake { get; set; }
    public Func<int?, Task>? CompleteKeepAwake { get; set; }
    public Func<string>? KeepAwakeDiagnostic { get; set; }

    public LenovoGpuWorkingModeService() : this(new LenovoGpuModeBackend(),
        UserSettingsStore.Get<int?>("GpuWorkingMode", null) is >= 0 and <= 2
            ? UserSettingsStore.Get("GpuWorkingMode", GpuWorkingMode.Default) : null,
        mode => UserSettingsStore.Set("GpuWorkingMode", mode)) { }

    internal LenovoGpuWorkingModeService(IGpuModeBackend backend, GpuWorkingMode? preference,
        Action<GpuWorkingMode> savePreference, Func<Task>? settle = null, TimeSpan? retryDelay = null)
    {
        _backend = backend;
        _logical = preference ?? GpuWorkingMode.Default;
        _hasPreference = preference.HasValue;
        _savePreference = savePreference;
        _settle = settle;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    }

    private void Read()
    {
        long revision = Interlocked.Increment(ref _revision);
        try
        {
            _capability = _backend.ProbeCapability();
            CapabilityDiagnostic = _capability > 0 ? $"CapabilitySupported Data={_capability}"
                : $"CapabilityUnsupported Data={_capability}";
            GpuModeDiagnostics.Write(CapabilityDiagnostic);
            if (_capability <= 0)
            {
                Snapshot = GpuWorkingSnapshot.Unavailable with { Available = true, Revision = revision };
                CancelRetry();
                return;
            }
        }
        catch (Exception ex)
        {
            CapabilityDiagnostic = "CapabilityProbeFailed LENOVO_GAMEZONE_DATA.IsSupportIGPUMode: " + ex;
            GpuModeDiagnostics.Write(CapabilityDiagnostic);
            Snapshot = GpuWorkingSnapshot.Unavailable with { Revision = revision };
            CancelRetry();
            return;
        }
        try
        {
            int effective = _backend.ReadMode();
            if (effective is not (0 or 1)) throw new InvalidDataException("GetIGPUModeStatus 返回未知状态。");
            if (!_hasPreference || _logical != GpuWorkingMode.Auto)
                _logical = effective == 1 ? GpuWorkingMode.IGPUOnly : GpuWorkingMode.Default;
            try { DisplayQuery = _backend.QueryDisplays(); }
            catch (Exception ex) { DisplayQuery = new(DgpuDisplayState.Unknown, NativeCode: ex.HResult, Detail: ex.ToString()); }
            Snapshot = new(true, _logical, effective, DisplayQuery.State, _backend.ReadPowerSource(),
                _backend.ReadNvidiaPresence(), GuardMessage(DisplayQuery.State), Revision: revision);
            if (DisplayQuery.State == DgpuDisplayState.Unknown) GpuModeDiagnostics.Write(DisplayQuery.ToString());
        }
        catch (Exception ex)
        {
            GpuModeDiagnostics.Write("RuntimeStateRead: " + ex);
            // A successful capability probe keeps the card visible on runtime failures.
            Snapshot = Snapshot with { Supported = true, Available = false, EffectiveMode = -1,
                DgpuDisplayState = DgpuDisplayState.Unknown, Message = "暂时无法读取显卡状态，未执行切换。", Revision = revision };
        }
    }

    internal static string GuardMessage(DgpuDisplayState state) => state switch
    {
        DgpuDisplayState.Connected => "外接显示器正在使用独立显卡输出。",
        DgpuDisplayState.Unknown => "无法检测独显显示状态，暂不允许关闭独立显卡。",
        _ => ""
    };

    public Task InitializeAsync(Func<bool> registerEvents) => RunAsync(async () =>
    {
        Read();
        if (Snapshot.Supported) AutoEventsAvailable = registerEvents();
        await ApplyPolicyAsync(true);
    });

    public Task RefreshAsync() => RefreshAsyncCore(() => { Read(); return Task.CompletedTask; });

    public Task SelectAsync(GpuWorkingMode mode)
    {
        CancelRetry();
        return RunAsync(async () =>
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            Read();
            if (!Snapshot.Supported) return;
            if (mode == GpuWorkingMode.Auto)
            {
                if (!AutoEventsAvailable) throw new InvalidOperationException("电源事件监听不可用，不能启用智能模式。");
                _logical = mode;
                _savePreference(mode);
                _hasPreference = true;
                Snapshot = Snapshot with { LogicalMode = mode };
                await ApplyPolicyAsync(true);
                return;
            }
            var result = await ExecuteAsync(mode == GpuWorkingMode.IGPUOnly ? 1 : 0);
            if (!result.Succeeded) throw new GpuModeOperationException(result);
            _logical = mode;
            _savePreference(mode);
            _hasPreference = true;
            Snapshot = Snapshot with { LogicalMode = mode };
        });
    }

    public Task HandleSystemChangeAsync()
    {
        CancelRetry();
        return RunAsync(async () => { Read(); await ApplyPolicyAsync(true); });
    }

    private async Task<GpuModeOperationResult> ExecuteAsync(int target, int? requiredPower = null,
        CancellationToken token = default)
    {
        long revision = Interlocked.Increment(ref _revision);
        var transaction = new GpuModeTransaction(_backend,
            () => SuspendKeepAwake?.Invoke() ?? throw new InvalidOperationException("未建立保活暂停链路。"),
            actual => CompleteKeepAwake?.Invoke(actual) ?? throw new InvalidOperationException("未建立保活恢复链路。"),
            _settle, diagnostic => ActiveDiagnostic = diagnostic);
        var result = await transaction.ExecuteAsync(target, _capability, KeepAwakeDiagnostic?.Invoke() ?? "not available", requiredPower, token);
        LastOperation = result;
        ActiveDiagnostic = "";
        DisplayQuery = result.Display;
        Snapshot = Snapshot with { EffectiveMode = result.EffectiveMode ?? -1,
            Available = result.EffectiveMode.HasValue, Message = result.Message, Revision = revision,
            DgpuDisplayState = target == 1 ? result.Display.State : Snapshot.DgpuDisplayState };
        if (_logical != GpuWorkingMode.Auto && result.EffectiveMode is { } effective)
            Snapshot = Snapshot with { LogicalMode = effective == 1 ? GpuWorkingMode.IGPUOnly : GpuWorkingMode.Default };
        return result;
    }

    private async Task ApplyPolicyAsync(bool allowRetry, CancellationToken token = default)
    {
        if (!Snapshot.Supported || !Snapshot.Available || !AutoEventsAvailable || _logical != GpuWorkingMode.Auto) return;
        int? target = GpuAutoPolicy.Target(Snapshot.PowerSource, Snapshot.DgpuDisplayState);
        if (target is null)
        {
            Snapshot = Snapshot with { LogicalMode = _logical, Message = Snapshot.PowerSource == 1
                ? Snapshot.DgpuDisplayState == DgpuDisplayState.Connected
                    ? "智能模式：因独显正在驱动外接显示器，当前暂保持现有模式。"
                    : "智能模式：无法确认独显显示状态，当前暂保持现有模式。"
                : "智能模式：供电状态未知，当前暂保持现有模式。" };
            GpuModeDiagnostics.Write(Snapshot.Message + " " + DisplayQuery);
            var failure = new GpuModeOperationFailure(Snapshot.PowerSource == 1 ? GpuModeOperationStage.DisplayGuard
                : GpuModeOperationStage.RuntimeStateRead, Snapshot.PowerSource == 1 ? "NVAPI" : "Power",
                Snapshot.Message, DisplayQuery.Detail, DisplayQuery.NativeCode);
            LastOperation = new(1, GpuModeOperationOutcome.Blocked, Snapshot.EffectiveMode, false, DisplayQuery,
                failure, null, $"Auto; capability={_capability}; power={Snapshot.PowerSource}; {failure}; {DisplayQuery}");
            return;
        }
        if (Snapshot.EffectiveMode == target) return;
        var result = await ExecuteAsync(target.Value, Snapshot.PowerSource, token);
        Snapshot = Snapshot with { LogicalMode = _logical };
        if (allowRetry && target == 1 && GpuAutoPolicy.ShouldRetry(result, Snapshot.PowerSource)) StartRetry();
    }

    private void StartRetry()
    {
        CancelRetry();
        var retry = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _retry, retry);
        previous?.Cancel();
        previous?.Dispose();
        _ = RetryAsync(retry.Token);
    }

    private async Task RetryAsync(CancellationToken token)
    {
        try
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(_retryDelay, token).ConfigureAwait(false);
                bool done = false;
                await RunAsync(async () =>
                {
                    if (token.IsCancellationRequested) { done = true; return; }
                    Read();
                    if (_logical != GpuWorkingMode.Auto || !Snapshot.Supported || !Snapshot.Available || Snapshot.PowerSource != 1)
                    { done = true; GpuModeDiagnostics.Write("Auto retry stopped by runtime state: " + Snapshot); return; }
                    GpuModeDiagnostics.Write($"Auto retry attempt={attempt}/5");
                    await ApplyPolicyAsync(false, token);
                    done = LastOperation is null || !GpuAutoPolicy.ShouldRetry(LastOperation, Snapshot.PowerSource) ||
                        Snapshot.EffectiveMode == 1;
                });
                if (done) return;
            }
            GpuModeDiagnostics.Write("Auto retries exhausted: " + LastOperation?.Diagnostics);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GpuModeDiagnostics.Write("Auto retry stopped: " + ex); }
    }

    public void CancelRetry()
    {
        var retry = Interlocked.Exchange(ref _retry, null);
        retry?.Cancel();
        retry?.Dispose();
    }

    protected override void Close() => CancelRetry();
}
