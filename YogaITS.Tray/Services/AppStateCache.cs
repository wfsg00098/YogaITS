namespace YogaITS.Tray.Services;

public sealed record AppRuntimeSnapshot(
    MachineInfoSnapshot MachineInfo,
    ItsCapabilitySnapshot ItsCapabilities,
    ItsModeSnapshot ItsMode,
    BatteryChargeSnapshot BatteryCharge,
    BatteryToggleSnapshot NightCharge,
    FlipToStartSnapshot FlipToStart,
    bool NvidiaGpuSupported,
    bool StartupEnabled);

public sealed class AppStateCache
{
    public event Action? Changed;
    private readonly object _gate = new();
    private AppRuntimeSnapshot _snapshot;
    private long _mutationGeneration;

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
        // Capability discovery and the first state read happen once while the
        // hidden tray host is starting. The tray menu never has to wait for
        // these reads before it can be shown.
        MachineInfoSnapshot machine = LenovoMachineInfoHelper.Read();
        ItsCapabilitySnapshot itsCapabilities =
            LenovoItsHelper.ReadCapabilities(machine.Model);

        Task<ItsModeSnapshot> itsTask = Task.Run(() =>
            LenovoItsHelper.ReadMode(itsCapabilities));
        Task<BatteryPair> batteryTask = Task.Run(ReadBatteryPair);
        Task<FlipToStartSnapshot> flipTask = Task.Run(
            LenovoFlipToStartHelper.Read);
        Task<bool> gpuTask = Task.Run(SafeHasNvidiaDisplayDevice);
        Task<bool> startupTask = Task.Run(StartupTaskHelper.IsEnabled);

        Task.WaitAll(
            itsTask,
            batteryTask,
            flipTask,
            gpuTask,
            startupTask);

        BatteryPair battery = batteryTask.Result;
        var snapshot = new AppRuntimeSnapshot(
            MachineInfo: machine,
            ItsCapabilities: itsCapabilities,
            ItsMode: itsTask.Result,
            BatteryCharge: battery.Charge,
            NightCharge: battery.NightCharge,
            FlipToStart: flipTask.Result,
            NvidiaGpuSupported: gpuTask.Result,
            StartupEnabled: startupTask.Result);

        return new AppStateCache(snapshot);
    }

    public async Task<AppRuntimeSnapshot> RefreshDynamicAsync()
    {
        AppRuntimeSnapshot basis;
        long generation;

        lock (_gate)
        {
            basis = _snapshot;
            generation = _mutationGeneration;
        }

        Task<ItsModeSnapshot> itsTask = basis.ItsCapabilities.Supported
            ? Task.Run(() => LenovoItsHelper.ReadMode(basis.ItsCapabilities))
            : Task.FromResult(basis.ItsMode);

        Task<BatteryPair> batteryTask = Task.Run(ReadBatteryPair);
        Task<FlipToStartSnapshot> flipTask = Task.Run(LenovoFlipToStartHelper.Read);

        Task<bool> startupTask = Task.Run(StartupTaskHelper.IsEnabled);
        Task<bool> gpuTask = Task.Run(SafeHasNvidiaDisplayDevice);

        await Task.WhenAll(
            itsTask,
            batteryTask,
            flipTask,
            startupTask,
            gpuTask).ConfigureAwait(false);

        ItsModeSnapshot its = itsTask.Result;
        BatteryPair battery = batteryTask.Result;
        FlipToStartSnapshot flip = flipTask.Result;
        bool startup = startupTask.Result;

        lock (_gate)
        {
            // A user action completed while these reads were in flight. Do not
            // overwrite that newer user-selected state with stale probe data.
            if (_mutationGeneration != generation)
                return _snapshot;

            ItsModeSnapshot mergedIts = its.Mode is not null
                ? its
                : _snapshot.ItsMode;

            _snapshot = _snapshot with
            {
                ItsMode = mergedIts,
                BatteryCharge = battery.Charge,
                NightCharge = battery.NightCharge,
                FlipToStart = flip,
                NvidiaGpuSupported = gpuTask.Result,
                StartupEnabled = startup
            };
            return _snapshot;
        }
    }

    public async Task<AppRuntimeSnapshot> RefreshItsModeAsync()
    {
        AppRuntimeSnapshot basis;
        long generation;

        lock (_gate)
        {
            basis = _snapshot;
            generation = _mutationGeneration;
        }

        if (!basis.ItsCapabilities.Supported)
            return basis;

        ItsModeSnapshot refreshed = await Task.Run(() =>
            LenovoItsHelper.ReadMode(basis.ItsCapabilities)).ConfigureAwait(false);

        if (refreshed.Mode is null)
            return Snapshot;

        lock (_gate)
        {
            if (_mutationGeneration != generation)
                return _snapshot;

            _snapshot = _snapshot with { ItsMode = refreshed };
            return _snapshot;
        }
    }

    public void UpdateItsMode(ItsModeSnapshot snapshot)
    {
        if (snapshot.Mode is null)
            return;

        Update(current => current with { ItsMode = snapshot });
    }

    public void UpdateBatteryCharge(BatteryChargeSnapshot snapshot)
    {
        if (!snapshot.Supported)
            return;

        Update(current => current with { BatteryCharge = snapshot });
    }

    public void UpdateBatterySelection(BatteryChargeSnapshot charge, BatteryToggleSnapshot night) =>
        Update(current => current with
        {
            BatteryCharge = charge.Supported ? charge : current.BatteryCharge,
            NightCharge = night.Supported ? night : current.NightCharge
        });

    public void UpdateNightCharge(BatteryToggleSnapshot snapshot)
    {
        if (!snapshot.Supported)
            return;

        Update(current => current with { NightCharge = snapshot });
    }

    public void UpdateFlipToStart(FlipToStartSnapshot snapshot)
    {
        if (!snapshot.Supported)
            return;

        Update(current => current with { FlipToStart = snapshot });
    }

    public void UpdateStartup(bool enabled) =>
        Update(current => current with { StartupEnabled = enabled });

    public void UpdateNvidiaPresence(bool present)
    {
        lock (_gate)
        {
            if (_snapshot.NvidiaGpuSupported == present) return;
            _snapshot = _snapshot with { NvidiaGpuSupported = present };
            _mutationGeneration++;
        }
        Changed?.Invoke();
    }

    private void Update(Func<AppRuntimeSnapshot, AppRuntimeSnapshot> update)
    {
        lock (_gate)
        {
            _snapshot = update(_snapshot);
            _mutationGeneration++;
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

    private static bool SafeHasNvidiaDisplayDevice()
    {
        try
        {
            return NvidiaGpuHelper.HasNvidiaDisplayDevice();
        }
        catch
        {
            return false;
        }
    }
}
