namespace YogaITS.Tray.Services;

internal enum GpuWorkingMode { Default, IGPUOnly, Auto }

internal sealed record GpuWorkingSnapshot(
    bool Supported, GpuWorkingMode LogicalMode, int EffectiveMode, DgpuDisplayState DgpuDisplayState,
    int PowerSource, bool NvidiaPresent, string Message, bool Available = true, long Revision = 0, int RawWmiMode = -1)
{
    public bool ModeConflict => GpuAutoPolicy.ConflictingObservation(RawWmiMode, NvidiaPresent, DgpuDisplayState);
    // Connected output confirms the GPU is awake even if Lenovo retains WMI=1.
    // This display state must not satisfy a SET0 request: its WMI acknowledgement is still required.
    public int ActualMode => !Available ? -1 : DgpuDisplayState == DgpuDisplayState.Connected ? 0 : EffectiveMode;
    public static GpuWorkingSnapshot Unavailable { get; } = new(false, GpuWorkingMode.Default, -1,
        DgpuDisplayState.Unknown, -1, false, "", false);
}

internal sealed class LenovoGpuWorkingModeService : SerializedFeatureService
{
    private readonly IGpuModeBackend _backend;
    private readonly Action<GpuWorkingMode> _savePreference;
    private readonly Func<Task>? _settle;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _observationDelay;
    private readonly Queue<string> _recentReads = new();
    private CancellationTokenSource? _retry;
    private CancellationTokenSource? _observation;
    private GpuWorkingMode _logical;
    private bool _hasPreference;
    private long _capability;
    private long _revision;
    public long StateRevision => Interlocked.Read(ref _revision);
    public GpuWorkingSnapshot Snapshot { get; private set; } = GpuWorkingSnapshot.Unavailable;
    public GpuModeOperationResult? LastOperation { get; private set; }
    public string ActiveDiagnostic { get; private set; } = "";
    public string RuntimeDiagnostic { get; private set; } = "";
    public bool IsObserving => Volatile.Read(ref _observation) is not null;
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
        Action<GpuWorkingMode> savePreference, Func<Task>? settle = null, TimeSpan? retryDelay = null,
        TimeSpan? observationDelay = null)
    {
        _backend = backend;
        _logical = preference ?? GpuWorkingMode.Default;
        _hasPreference = preference.HasValue;
        _savePreference = savePreference;
        _settle = settle;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        _observationDelay = observationDelay ?? TimeSpan.FromMilliseconds(750);
    }

    private static string ConflictMessage(DgpuDisplayState display) => display == DgpuDisplayState.Connected
        ? "外接显示器正在使用独立显卡输出，当前保持双显模式。"
        : "WMI 报告集显，但独显设备仍然存在；实际模式待确认，尚未确认独显关闭。";

    private void RecordRead(string source)
    {
        var state = Snapshot;
        string entry = $"time={DateTimeOffset.UtcNow:O}; source={source}; WMI={state.RawWmiMode}; " +
            $"effective={state.EffectiveMode}; actual={state.ActualMode}; nvidiaPresent={state.NvidiaPresent}; conflict={state.ModeConflict}; NVAPI={state.DgpuDisplayState}; " +
            $"nativeCode={DisplayQuery.NativeCode}; power={state.PowerSource}; revision={state.Revision}";
        _recentReads.Enqueue(entry);
        while (_recentReads.Count > 8) _recentReads.Dequeue();
        RuntimeDiagnostic = string.Join(Environment.NewLine, _recentReads);
        GpuModeDiagnostics.Write(entry);
    }

    private string ObservationMessage(int raw, bool nvidiaPresent, DgpuDisplayState display, int power)
    {
        if (_logical == GpuWorkingMode.Auto && power == 1 && display == DgpuDisplayState.Connected)
            return "智能模式：因独显正在驱动外接显示器，当前暂保持双显模式。";
        if (GpuAutoPolicy.ConflictingObservation(raw, nvidiaPresent, display)) return ConflictMessage(display);
        // An unavailable NVIDIA API after confirmed ejection is not a failed guard.
        if (raw == 1) return "";
        if (_logical == GpuWorkingMode.Auto && power == 1 && display == DgpuDisplayState.Unknown)
            return "智能模式：无法确认独显显示状态，当前暂保持现有模式。";
        return GuardMessage(display);
    }

    private void Read(string source = "Refresh")
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
                DisplayQuery = new(DgpuDisplayState.Unknown, Detail: "Capability unsupported; display query not run.");
                CancelRetry();
                RecordRead(source);
                return;
            }
        }
        catch (Exception ex)
        {
            CapabilityDiagnostic = "CapabilityProbeFailed LENOVO_GAMEZONE_DATA.IsSupportIGPUMode: " + ex;
            GpuModeDiagnostics.Write(CapabilityDiagnostic);
            Snapshot = GpuWorkingSnapshot.Unavailable with { Revision = revision };
            DisplayQuery = new(DgpuDisplayState.Unknown, Detail: "Capability probe failed; display query not run.");
            CancelRetry();
            RecordRead(source);
            return;
        }
        try
        {
            int effective = _backend.ReadMode();
            if (effective is not (0 or 1)) throw new InvalidDataException("GetIGPUModeStatus 返回未知状态。");
            try { DisplayQuery = _backend.QueryDisplays(); }
            catch (Exception ex) { DisplayQuery = new(DgpuDisplayState.Unknown, NativeCode: ex.HResult, Detail: ex.ToString()); }
            bool present = _backend.ReadNvidiaPresence();
            bool conflict = GpuAutoPolicy.ConflictingObservation(effective, present, DisplayQuery.State);
            if ((!_hasPreference || _logical != GpuWorkingMode.Auto) && !conflict)
                _logical = effective == 1 ? GpuWorkingMode.IGPUOnly : GpuWorkingMode.Default;
            int power = _backend.ReadPowerSource();
            Snapshot = new(true, _logical, GpuAutoPolicy.ConfirmedMode(effective, present, DisplayQuery.State), DisplayQuery.State, power,
                present, ObservationMessage(effective, present, DisplayQuery.State, power),
                Revision: revision, RawWmiMode: effective);
            if (DisplayQuery.State == DgpuDisplayState.Unknown) GpuModeDiagnostics.Write(DisplayQuery.ToString());
        }
        catch (Exception ex)
        {
            GpuModeDiagnostics.Write("RuntimeStateRead: " + ex);
            // A successful capability probe keeps the card visible on runtime failures.
            Snapshot = Snapshot with { Supported = true, Available = false, EffectiveMode = -1,
                DgpuDisplayState = DgpuDisplayState.Unknown, Message = "暂时无法读取显卡状态，未执行切换。",
                Revision = revision, RawWmiMode = -1 };
        }
        RecordRead(source);
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
        if (Snapshot.ModeConflict) StartObservation();
    });

    public Task RefreshAsync() => RefreshAsyncCore(() =>
    {
        Read();
        if (Snapshot.ModeConflict && !IsObserving) StartObservation();
        return Task.CompletedTask;
    });

    public Task SelectAsync(GpuWorkingMode mode)
    {
        CancelRetry();
        CancelObservation();
        return RunAsync(async () =>
        {
            CancelObservation();
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

    public Task HandleSystemChangeAsync(bool displayChanged = false)
    {
        CancelRetry();
        CancelObservation();
        return RunAsync(async () =>
        {
            CancelObservation();
            Read(displayChanged ? "Display event" : "Power/resume event");
            await ApplyPolicyAsync(true);
            if (displayChanged || Snapshot.ModeConflict) StartObservation();
        });
    }

    private async Task<GpuModeOperationResult> ExecuteAsync(int target, int? requiredPower = null,
        CancellationToken token = default)
    {
        long revision = Interlocked.Increment(ref _revision);
        var transaction = new GpuModeTransaction(_backend,
            () => SuspendKeepAwake?.Invoke() ?? throw new InvalidOperationException("未建立保活暂停链路。"),
            actual => CompleteKeepAwake?.Invoke(actual) ?? throw new InvalidOperationException("未建立保活恢复链路。"),
            _settle, diagnostic => ActiveDiagnostic = diagnostic);
        var result = await transaction.ExecuteAsync(target, _capability, KeepAwakeDiagnostic?.Invoke() ?? "not available",
            requiredPower, token, DisplayQuery);
        LastOperation = result;
        ActiveDiagnostic = "";
        DisplayQuery = result.Display;
        Snapshot = Snapshot with { EffectiveMode = result.EffectiveMode ?? -1,
            Available = result.RawWmiMode.HasValue && result.NvidiaPresent.HasValue,
            NvidiaPresent = result.NvidiaPresent ?? Snapshot.NvidiaPresent,
            Message = result.Message, Revision = revision,
            RawWmiMode = result.RawWmiMode ?? -1, DgpuDisplayState = result.Display.State };
        if (Snapshot.ModeConflict && result.Failure?.Stage != GpuModeOperationStage.EffectiveStateVerify)
            Snapshot = Snapshot with { Message = ConflictMessage(result.Display.State) };
        if (_logical != GpuWorkingMode.Auto && result.EffectiveMode is { } effective)
            Snapshot = Snapshot with { LogicalMode = effective == 1 ? GpuWorkingMode.IGPUOnly : GpuWorkingMode.Default };
        RecordRead("Transaction result");
        return result;
    }

    private async Task ApplyPolicyAsync(bool allowRetry, CancellationToken token = default)
    {
        if (!Snapshot.Supported || !Snapshot.Available || !AutoEventsAvailable || _logical != GpuWorkingMode.Auto) return;
        int? desired = GpuAutoPolicy.DesiredTarget(Snapshot.PowerSource);
        if (desired is { } already && Snapshot.EffectiveMode == already && !Snapshot.ModeConflict)
        {
            Snapshot = Snapshot with { Message = ObservationMessage(Snapshot.RawWmiMode, Snapshot.NvidiaPresent, Snapshot.DgpuDisplayState, Snapshot.PowerSource) };
            LastOperation = new(already, GpuModeOperationOutcome.Success, already, false, DisplayQuery, null, null,
                $"time={DateTimeOffset.UtcNow:O}; Auto target={already}; WMI={Snapshot.RawWmiMode}; nvidiaPresent={Snapshot.NvidiaPresent}; already at target; no SET; NVAPI={DisplayQuery.State}",
                Snapshot.NvidiaPresent);
            return;
        }
        int? target = GpuAutoPolicy.Target(Snapshot.PowerSource, Snapshot.DgpuDisplayState);
        if (target is null)
        {
            Snapshot = Snapshot with { LogicalMode = _logical, Message = Snapshot.PowerSource == 1
                ? ObservationMessage(Snapshot.RawWmiMode, Snapshot.NvidiaPresent, Snapshot.DgpuDisplayState, Snapshot.PowerSource)
                : "智能模式：供电状态未知，当前暂保持现有模式。" };
            GpuModeDiagnostics.Write(Snapshot.Message + " " + DisplayQuery);
            var failure = new GpuModeOperationFailure(Snapshot.PowerSource == 1 ? GpuModeOperationStage.DisplayGuard
                : GpuModeOperationStage.RuntimeStateRead, Snapshot.PowerSource == 1 ? "NVAPI" : "Power",
                Snapshot.Message, DisplayQuery.Detail, DisplayQuery.NativeCode);
            LastOperation = new(1, GpuModeOperationOutcome.Blocked, Snapshot.RawWmiMode, false, DisplayQuery,
                failure, null, $"time={DateTimeOffset.UtcNow:O}; Auto; capability={_capability}; WMI={Snapshot.RawWmiMode}; " +
                $"effective={Snapshot.EffectiveMode}; actual={Snapshot.ActualMode}; nvidiaPresent={Snapshot.NvidiaPresent}; power={Snapshot.PowerSource}; {failure}; {DisplayQuery}",
                Snapshot.NvidiaPresent);
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

    private void StartObservation()
    {
        CancelObservation();
        if (!Snapshot.Supported) return;
        var pending = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        Interlocked.Exchange(ref _observation, pending);
        _ = ObserveAsync(pending, pending.Token);
    }

    private async Task ObserveAsync(CancellationTokenSource pending, CancellationToken token)
    {
        try
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(_observationDelay, token).ConfigureAwait(false);
                await RefreshAsyncCore(() =>
                {
                    if (!token.IsCancellationRequested) Read($"Display observation {attempt}/5 (read-only)");
                    return Task.CompletedTask;
                });
                if (!Snapshot.Supported) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GpuModeDiagnostics.Write("Display observation stopped: " + ex); }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _observation, null, pending), pending)) pending.Dispose();
        }
    }

    private void CancelObservation()
    {
        var pending = Interlocked.Exchange(ref _observation, null);
        pending?.Cancel();
        pending?.Dispose();
    }

    public void CancelPendingOperations() { CancelRetry(); CancelObservation(); }
    protected override void Close() => CancelPendingOperations();
}
