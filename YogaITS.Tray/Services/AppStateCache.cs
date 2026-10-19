namespace YogaITS.Tray.Services;

public sealed record AppRuntimeSnapshot(
    MachineInfoSnapshot MachineInfo,
    ItsCapabilitySnapshot ItsCapabilities,
    ItsModeSnapshot ItsMode,
    BatteryChargeSnapshot BatteryCharge,
    BatteryToggleSnapshot NightCharge,
    FlipToStartSnapshot FlipToStart,
    bool NvidiaGpuSupported,
    bool StartupEnabled,
    bool ItsAvailable = true, bool BatteryAvailable = true, bool NightAvailable = true,
    bool FlipAvailable = true, bool StartupAvailable = true);

public sealed class AppStateCache
{
    public event Action? Changed;
    private readonly object _gate = new();
    private AppRuntimeSnapshot _snapshot;
    private const int ItsField = 0, ChargeField = 1, NightField = 2, FlipField = 3, StartupField = 4, GpuField = 5;
    private readonly SnapshotRevisions _revisions = new(6);
    private int _capabilityAttempts;
    private readonly BoundedProbe<MachineInfoSnapshot> _machineProbe = new();
    private readonly BoundedProbe<ItsCapabilitySnapshot> _capabilityProbe = new();
    private readonly BoundedProbe<ItsModeSnapshot> _itsProbe = new();
    private readonly BoundedProbe<BatteryPair> _batteryProbe = new();
    private readonly BoundedProbe<FlipToStartSnapshot> _flipProbe = new();
    private readonly BoundedProbe<bool?> _startupProbe = new();
    private readonly BoundedProbe<bool?> _gpuProbe = new();

    private sealed record BatteryPair(
        BatteryChargeSnapshot Charge,
        BatteryToggleSnapshot NightCharge);

    private AppStateCache(AppRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public AppRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public static AppStateCache Initialize()
    {
        // No hardware, WMI or schtasks work on the XAML startup thread.
        return new AppStateCache(new(new("", "", ""), default, new(null, null, "正在检测"),
            new(false, BatteryChargeMode.Normal, "正在检测"), new(false, false), new(false, false, ""),
            false, false, false, false, false, false, false));
    }

    public async Task<AppRuntimeSnapshot> RefreshDynamicAsync()
    {
        AppRuntimeSnapshot basis;
        long[] observed;

        lock (_gate)
        {
            basis = _snapshot;
            observed = _revisions.Capture();
        }

        var batteryTask = _batteryProbe.ReadAsync(ReadBatteryPair);
        var flipTask = _flipProbe.ReadAsync(LenovoFlipToStartHelper.Read);
        var startupTask = _startupProbe.ReadAsync(StartupTaskHelper.ReadEnabled);
        var gpuTask = _gpuProbe.ReadAsync(SafeHasNvidiaDisplayDevice);
        var machine = basis.MachineInfo;
        if (string.IsNullOrWhiteSpace(machine.Model))
        {
            var result = await _machineProbe.ReadAsync(LenovoMachineInfoHelper.Read);
            if (result.Success) machine = result.Value;
        }
        var capabilities = basis.ItsCapabilities;
        if (!capabilities.Supported && _capabilityAttempts < 3)
        {
            _capabilityAttempts++;
            var result = await _capabilityProbe.ReadAsync(() => LenovoItsHelper.ReadCapabilities(machine.Model));
            if (result.Success) capabilities = result.Value;
        }
        var itsTask = capabilities.Supported
            ? _itsProbe.ReadAsync(() => LenovoItsHelper.ReadMode(capabilities))
            : Task.FromResult(new ProbeResult<ItsModeSnapshot>(true, basis.ItsMode));

        await Task.WhenAll(
            itsTask,
            batteryTask,
            flipTask,
            startupTask,
            gpuTask).ConfigureAwait(false);

        ItsModeSnapshot its = itsTask.Result.Success ? itsTask.Result.Value : new(null, null, "暂不可读");
        BatteryPair battery = batteryTask.Result.Success ? batteryTask.Result.Value
            : new(new(false, BatteryChargeMode.Normal, "暂不可读"), new(false, false));
        FlipToStartSnapshot flip = flipTask.Result.Success ? flipTask.Result.Value : new(false, false, "");
        bool? startup = startupTask.Result.Success ? startupTask.Result.Value : null;
        bool? gpuPresent = gpuTask.Result.Success ? gpuTask.Result.Value : null;

        lock (_gate)
        {
            bool useIts = _revisions.IsCurrent(ItsField, observed);
            bool useCharge = _revisions.IsCurrent(ChargeField, observed);
            bool useNight = _revisions.IsCurrent(NightField, observed);
            bool useFlip = _revisions.IsCurrent(FlipField, observed);
            bool useStartup = _revisions.IsCurrent(StartupField, observed);
            bool useGpu = _revisions.IsCurrent(GpuField, observed);
            ItsModeSnapshot mergedIts = useIts && its.Mode is not null
                ? its
                : _snapshot.ItsMode;

            _snapshot = _snapshot with
            {
                ItsMode = mergedIts,
                MachineInfo = machine,
                ItsCapabilities = capabilities,
                BatteryCharge = useCharge && battery.Charge.Supported ? battery.Charge : _snapshot.BatteryCharge,
                NightCharge = useNight && battery.NightCharge.Supported ? battery.NightCharge : _snapshot.NightCharge,
                FlipToStart = useFlip && flip.Supported ? flip : _snapshot.FlipToStart,
                NvidiaGpuSupported = useGpu ? gpuPresent ?? _snapshot.NvidiaGpuSupported : _snapshot.NvidiaGpuSupported,
                StartupEnabled = useStartup ? startup ?? _snapshot.StartupEnabled : _snapshot.StartupEnabled,
                ItsAvailable = useIts ? its.Mode is not null : _snapshot.ItsAvailable,
                BatteryAvailable = useCharge ? battery.Charge.Supported : _snapshot.BatteryAvailable,
                NightAvailable = useNight ? battery.NightCharge.Supported : _snapshot.NightAvailable,
                FlipAvailable = useFlip ? flip.Supported : _snapshot.FlipAvailable,
                StartupAvailable = useStartup ? startup is not null : _snapshot.StartupAvailable
            };
            return _snapshot;
        }
    }

    public void UpdateItsMode(ItsModeSnapshot snapshot)
    {
        if (snapshot.Mode is null)
            return;

        Update(current => current with { ItsMode = snapshot, ItsAvailable = true }, ItsField);
    }

    public void UpdateBatteryCharge(BatteryChargeSnapshot snapshot)
    {
        if (!snapshot.Supported)
            return;

        Update(current => current with { BatteryCharge = snapshot, BatteryAvailable = true }, ChargeField);
    }

    public void UpdateBatterySelection(BatteryChargeSnapshot charge, BatteryToggleSnapshot night) =>
        Update(current => current with
        {
            BatteryCharge = charge.Supported ? charge : current.BatteryCharge,
            NightCharge = night.Supported ? night : current.NightCharge,
            BatteryAvailable = charge.Supported, NightAvailable = night.Supported
        }, ChargeField, NightField);

    public void UpdateNightCharge(BatteryToggleSnapshot snapshot)
    {
        if (!snapshot.Supported)
            return;

        Update(current => current with { NightCharge = snapshot, NightAvailable = true }, NightField);
    }

    public void UpdateFlipToStart(FlipToStartSnapshot snapshot)
    {
        if (!snapshot.Supported)
            return;

        Update(current => current with { FlipToStart = snapshot, FlipAvailable = true }, FlipField);
    }

    public void UpdateStartup(bool enabled) =>
        Update(current => current with { StartupEnabled = enabled, StartupAvailable = true }, StartupField);

    public void UpdateNvidiaPresence(bool present)
    {
        lock (_gate)
        {
            if (_snapshot.NvidiaGpuSupported == present) return;
            _snapshot = _snapshot with { NvidiaGpuSupported = present };
            _revisions.Changed(GpuField);
        }
        Changed?.Invoke();
    }

    private void Update(Func<AppRuntimeSnapshot, AppRuntimeSnapshot> update, params int[] fields)
    {
        lock (_gate)
        {
            _snapshot = update(_snapshot);
            _revisions.Changed(fields);
        }
        Changed?.Invoke();
    }

    private static BatteryPair ReadBatteryPair()
    {
        // Keep the two EnergyDrv battery reads in one worker. They are still
        // concurrent with ITS / flip-to-start / startup reads, but are not fired
        // at the driver simultaneously from two separate threads.
        BatteryChargeSnapshot charge = LenovoBatteryHelper.ReadChargeMode();
        BatteryToggleSnapshot night = LenovoBatteryHelper.ReadNightCharge();
        return new BatteryPair(charge, night);
    }

    private static bool? SafeHasNvidiaDisplayDevice()
    {
        try
        {
            return NvidiaGpuHelper.HasNvidiaDisplayDevice();
        }
        catch
        {
            return null;
        }
    }
}
