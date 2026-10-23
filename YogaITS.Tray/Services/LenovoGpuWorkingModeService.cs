namespace YogaITS.Tray.Services;

internal enum GpuWorkingMode { Default, IGPUOnly, Auto }

internal sealed record GpuWorkingSnapshot(
    bool Supported, GpuWorkingMode PreferredMode, int EffectiveMode, DgpuDisplayState DgpuDisplayState,
    int PowerSource, bool NvidiaPresent, string Message, bool Available = true, long Revision = 0, int RawWmiMode = -1)
{
    public bool ModeConflict => GpuAutoPolicy.ConflictingObservation(RawWmiMode, NvidiaPresent, DgpuDisplayState);
    // Successful NVAPI enumeration plus PnP presence confirms an available dGPU,
    // independently of display use or Lenovo's retained setting. SET acknowledgement is separate.
    public int ActualMode => !Available ? -1 : (DgpuDisplayState == DgpuDisplayState.Connected ||
        NvidiaPresent && DgpuDisplayState == DgpuDisplayState.Disconnected) ? 0 : EffectiveMode;
    public int? DesiredMode => GpuAutoPolicy.DesiredTarget(PreferredMode, PowerSource);
    public bool PreferenceSatisfied => DesiredMode is { } target && EffectiveMode == target && !ModeConflict;
    public bool CanSelectIGpu => DgpuDisplayState != DgpuDisplayState.Connected;
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
    private GpuWorkingMode _preference;
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
        _preference = preference ?? GpuWorkingMode.Default;
        _hasPreference = preference.HasValue;
        _savePreference = savePreference;
        _settle = settle;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        _observationDelay = observationDelay ?? TimeSpan.FromMilliseconds(750);
    }

    private void RecordRead(string source)
    {
        var state = Snapshot;
        string entry = $"time={DateTimeOffset.UtcNow:O}; source={source}; WMI={state.RawWmiMode}; " +
            $"preference={state.PreferredMode}; target={state.DesiredMode}; satisfied={state.PreferenceSatisfied}; " +
            $"effective={state.EffectiveMode}; actual={state.ActualMode}; nvidiaPresent={state.NvidiaPresent}; conflict={state.ModeConflict}; NVAPI={state.DgpuDisplayState}; " +
            $"nativeCode={DisplayQuery.NativeCode}; power={state.PowerSource}; revision={state.Revision}";
        _recentReads.Enqueue(entry);
        while (_recentReads.Count > 8) _recentReads.Dequeue();
        RuntimeDiagnostic = string.Join(Environment.NewLine, _recentReads);
        GpuModeDiagnostics.Write(entry);
    }

    private string ObservationMessage(int raw, bool nvidiaPresent, DgpuDisplayState display, int power)
    {
        int? desired = GpuAutoPolicy.DesiredTarget(_preference, power);
        if (desired is null) return "供电状态未知，保留智能模式偏好，暂不切换。";
        if (GpuAutoPolicy.ConfirmedMode(raw, nvidiaPresent, display) == desired) return "";
        if (desired == 0) return "双显模式偏好已保留，尚未确认联想模式设置生效。";
        return display switch
        {
            DgpuDisplayState.Connected => "外接显示器正在使用独显，暂时保持双显；断开后将继续尝试集显。",
            DgpuDisplayState.Unknown => "无法检测独显显示状态，保留集显偏好，暂不关闭独显。",
            _ => "独显设备仍可用，尚未达成集显模式偏好。"
        };
    }

    private bool AdaptPreferenceToDisplay()
    {
        if (!Snapshot.Supported || !Snapshot.Available || Snapshot.CanSelectIGpu || _preference != GpuWorkingMode.IGPUOnly)
            return false;
        // The sole automatic preference change: an attached dGPU display makes iGPU-only unavailable.
        _savePreference(GpuWorkingMode.Auto);
        _preference = GpuWorkingMode.Auto;
        _hasPreference = true;
        Snapshot = Snapshot with { PreferredMode = _preference,
            Message = ObservationMessage(Snapshot.RawWmiMode, Snapshot.NvidiaPresent, Snapshot.DgpuDisplayState, Snapshot.PowerSource) };
        RecordRead("Connected display: preference IGPUOnly -> Auto");
        return true;
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
                Snapshot = GpuWorkingSnapshot.Unavailable with { PreferredMode = _preference, Available = true, Revision = revision };
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
            Snapshot = GpuWorkingSnapshot.Unavailable with { PreferredMode = _preference, Revision = revision };
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
            if (!_hasPreference)
            {
                _preference = effective == 1 ? GpuWorkingMode.IGPUOnly : GpuWorkingMode.Default;
                _hasPreference = true;
            }
            int power = _backend.ReadPowerSource();
            Snapshot = new(true, _preference, GpuAutoPolicy.ConfirmedMode(effective, present, DisplayQuery.State), DisplayQuery.State, power,
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

    public Task InitializeAsync(Func<bool> registerEvents) => RunAsync(async () =>
    {
        Read();
        if (Snapshot.Supported) AutoEventsAvailable = registerEvents();
        await ApplyPolicyAsync(true);
        if (Snapshot.ModeConflict) StartObservation();
    });

    public Task RefreshReadOnlyAsync() => RefreshAsyncCore(() =>
    {
        Read("Read-only refresh");
        return Task.CompletedTask;
    });

    public Task RefreshAsync() => RefreshAsyncCore(async () =>
    {
        Read();
        if (AdaptPreferenceToDisplay()) await ApplyPolicyAsync(true);
        if (Snapshot.ModeConflict && !IsObserving) StartObservation();
    });

    public Task SelectAsync(GpuWorkingMode mode)
    {
        CancelRetry();
        CancelObservation();
        return RunAsync(async () =>
        {
            CancelRetry();
            CancelObservation();
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            Read();
            if (!Snapshot.Supported) return;
            if (mode == GpuWorkingMode.IGPUOnly && !Snapshot.CanSelectIGpu)
            {
                await ApplyPolicyAsync(true);
                return;
            }
            if (mode == GpuWorkingMode.Auto && !AutoEventsAvailable)
                throw new InvalidOperationException("电源事件监听不可用，不能启用智能模式。");
            _savePreference(mode);
            _preference = mode;
            _hasPreference = true;
            Snapshot = Snapshot with { PreferredMode = mode };
            RecordRead("User preference");
            var result = await ApplyPolicyAsync(mode == GpuWorkingMode.Auto);
            if (result?.Outcome == GpuModeOperationOutcome.Blocked) StartObservation();
            // A click saves intent even if execution fails. Manual clicks SET at most once.
            if (mode != GpuWorkingMode.Auto && result is { Succeeded: false, Outcome: not GpuModeOperationOutcome.Blocked })
                throw new GpuModeOperationException(result);
        });
    }

    public Task HandleSystemChangeAsync(bool displayChanged = false)
    {
        CancelRetry();
        CancelObservation();
        return RunAsync(async () =>
        {
            CancelRetry();
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
        result = result with { Diagnostics = $"preference={_preference}; target={target}" + Environment.NewLine + result.Diagnostics };
        LastOperation = result;
        ActiveDiagnostic = "";
        DisplayQuery = result.Display;
        Snapshot = Snapshot with { PreferredMode = _preference, EffectiveMode = result.EffectiveMode ?? -1,
            Available = result.RawWmiMode.HasValue && result.NvidiaPresent.HasValue,
            NvidiaPresent = result.NvidiaPresent ?? Snapshot.NvidiaPresent,
            Message = result.Message, Revision = revision,
            RawWmiMode = result.RawWmiMode ?? -1, DgpuDisplayState = result.Display.State };
        if (string.IsNullOrEmpty(Snapshot.Message))
            Snapshot = Snapshot with { Message = ObservationMessage(Snapshot.RawWmiMode, Snapshot.NvidiaPresent, Snapshot.DgpuDisplayState, Snapshot.PowerSource) };
        RecordRead("Transaction result");
        return result;
    }

    private async Task<GpuModeOperationResult?> ApplyPolicyAsync(bool allowRetry, CancellationToken token = default)
    {
        if (!Snapshot.Supported || !Snapshot.Available) return null;
        AdaptPreferenceToDisplay();
        if (_preference == GpuWorkingMode.Auto && !AutoEventsAvailable) return null;
        int? desired = Snapshot.DesiredMode;
        if (Snapshot.PreferenceSatisfied && desired is { } already)
        {
            Snapshot = Snapshot with { Message = ObservationMessage(Snapshot.RawWmiMode, Snapshot.NvidiaPresent, Snapshot.DgpuDisplayState, Snapshot.PowerSource) };
            LastOperation = new(already, GpuModeOperationOutcome.Success, already, false, DisplayQuery, null, null,
                $"time={DateTimeOffset.UtcNow:O}; preference={_preference}; target={already}; WMI={Snapshot.RawWmiMode}; nvidiaPresent={Snapshot.NvidiaPresent}; already at target; no SET; NVAPI={DisplayQuery.State}",
                Snapshot.NvidiaPresent);
            return LastOperation;
        }
        if (GpuAutoPolicy.Target(_preference, Snapshot.PowerSource, Snapshot.DgpuDisplayState) is not { } target)
        {
            Snapshot = Snapshot with { Message = ObservationMessage(Snapshot.RawWmiMode, Snapshot.NvidiaPresent, Snapshot.DgpuDisplayState, Snapshot.PowerSource) };
            GpuModeDiagnostics.Write(Snapshot.Message + " " + DisplayQuery);
            var failure = new GpuModeOperationFailure(desired is not null ? GpuModeOperationStage.DisplayGuard
                : GpuModeOperationStage.RuntimeStateRead, desired is not null ? "NVAPI" : "Power",
                Snapshot.Message, DisplayQuery.Detail, DisplayQuery.NativeCode);
            LastOperation = new(desired ?? 1, GpuModeOperationOutcome.Blocked, Snapshot.RawWmiMode, false, DisplayQuery,
                failure, null, $"time={DateTimeOffset.UtcNow:O}; preference={_preference}; target={desired}; capability={_capability}; WMI={Snapshot.RawWmiMode}; " +
                $"effective={Snapshot.EffectiveMode}; actual={Snapshot.ActualMode}; nvidiaPresent={Snapshot.NvidiaPresent}; power={Snapshot.PowerSource}; {failure}; {DisplayQuery}",
                Snapshot.NvidiaPresent);
            return LastOperation;
        }
        var result = await ExecuteAsync(target, _preference == GpuWorkingMode.Auto ? Snapshot.PowerSource : null, token);
        // The fresh transaction guard can discover a display connected after the initial read.
        if (AdaptPreferenceToDisplay()) return await ApplyPolicyAsync(allowRetry, token);
        if (allowRetry && GpuAutoPolicy.ShouldRetry(result, Snapshot.PowerSource, _preference)) StartRetry();
        return result;
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
                    if (!Snapshot.Supported || !Snapshot.Available || Snapshot.DesiredMode != 1)
                    { done = true; GpuModeDiagnostics.Write("Preference retry stopped by runtime state: " + Snapshot); return; }
                    GpuModeDiagnostics.Write($"Preference retry attempt={attempt}/5; preference={_preference}");
                    await ApplyPolicyAsync(false, token);
                    done = LastOperation is null || !GpuAutoPolicy.ShouldRetry(LastOperation, Snapshot.PowerSource, _preference) ||
                        Snapshot.PreferenceSatisfied;
                });
                if (done) return;
            }
            GpuModeDiagnostics.Write("Preference retries exhausted: " + LastOperation?.Diagnostics);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GpuModeDiagnostics.Write("Preference retry stopped: " + ex); }
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
        // A blocked preference may be attempted once if this event's constraints settle.
        // A SET failure belongs to the separate bounded retry path, never to observation.
        bool deferred = LastOperation is { Outcome: GpuModeOperationOutcome.Blocked, SetInvoked: false };
        _ = ObserveAsync(pending, pending.Token, deferred);
    }

    private async Task ObserveAsync(CancellationTokenSource pending, CancellationToken token, bool deferred)
    {
        try
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(_observationDelay, token).ConfigureAwait(false);
                await RunAsync(async () =>
                {
                    if (token.IsCancellationRequested) return;
                    Read($"Display observation {attempt}/5");
                    if (AdaptPreferenceToDisplay())
                    {
                        var result = await ApplyPolicyAsync(true, token);
                        deferred = result is { Outcome: GpuModeOperationOutcome.Blocked, SetInvoked: false };
                    }
                    else if (deferred && Snapshot.Supported && Snapshot.Available && !Snapshot.PreferenceSatisfied &&
                        GpuAutoPolicy.Target(_preference, Snapshot.PowerSource, Snapshot.DgpuDisplayState) is not null)
                    {
                        deferred = false;
                        await ApplyPolicyAsync(true, token);
                    }
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
