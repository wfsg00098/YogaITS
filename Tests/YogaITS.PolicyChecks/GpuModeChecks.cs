using System.Runtime.InteropServices;
using YogaITS.Tray.Services;

internal static class GpuModeChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAILED GPU mode check: " + message);
            checks++;
        }
        Check(Marshal.SizeOf<NvGpuDisplayId>() == 16 && Marshal.OffsetOf<NvGpuDisplayId>(nameof(NvGpuDisplayId.Flags)).ToInt32() == 12 &&
            NvGpuDisplayId.Version == 0x30010, "NVIDIA's display-ID struct is 16 bytes with revision 3, not revision 2");
        foreach (uint flags in new uint[] { 0, 1, 4, 16, 1u << 17, 1u << 6, (1u << 6) | 4 })
            Check(new NvGpuDisplayId { Flags = flags }.IsConnected == ((flags & 64) != 0),
                "connected flag is bit6; active/OS-visible/physical bits alone are not connected");
        foreach (object? invalid in new object?[] { null, "3", true, 1.0, new uint[] { 3 } })
        {
            bool failed = false;
            try { LenovoGpuModeBackend.DecodeData(invalid); } catch (InvalidDataException) { failed = true; }
            Check(failed, "WMI missing or unexpected Data types cannot establish capability");
        }
        Check(LenovoGpuModeBackend.DecodeData(3u) == 3 && LenovoGpuModeBackend.DecodeData(0) == 0 &&
            LenovoGpuModeBackend.DecodeData(uint.MaxValue) == uint.MaxValue,
            "WMI accepts numeric UInt32/Int32 without converting strings or Boolean to capability");
        foreach (var api in new[]
        {
            new FakeNvapi(), new FakeNvapi { DisplayCount = 2 }, new FakeNvapi { Gpus = 2, ConnectedGpu = 2 }
        })
        {
            var result = NvidiaDisplayQuery.Query(() => api);
            Check(result.State == (api.ConnectedGpu == 0 ? DgpuDisplayState.Disconnected : DgpuDisplayState.Connected) && api.Disposed,
                "queries all GPUs and releases the API session; adapter presence is not display connection");
            Check(api.VersionsValid && (api.DisplayCount != 0 || api.ConnectedGpu != 0 || api.DataCalls == 0),
                "every data slot is versioned, and zero count does not trigger a data query");
        }
        foreach (int failure in new[] { 1, 2, 3, 4 })
        {
            var api = new FakeNvapi { FailStage = failure, DisplayCount = 1 };
            var result = NvidiaDisplayQuery.Query(() => api);
            Check(result.State == DgpuDisplayState.Unknown && result.NativeCode == -123 && api.Disposed &&
                result.Detail!.Contains(failure == 1 ? "Initialize" : failure == 2 ? "EnumPhysicalGPUs" : "GetConnectedDisplayIds"),
                "initialization/enumeration/count/data failures preserve stage and raw NVAPI status");
        }
        foreach (var api in new[] { new FakeNvapi { Gpus = 0 }, new FakeNvapi { Gpus = 65 },
            new FakeNvapi { DisplayCount = 5000 }, new FakeNvapi { DisplayCount = 1, GrowCount = true } })
            Check(NvidiaDisplayQuery.Query(() => api).State == DgpuDisplayState.Unknown && api.Disposed,
                "invalid native counts and hot-plug buffer growth fail closed");
        Check(NvidiaDisplayQuery.Query(() => throw new DllNotFoundException("fixture")).State == DgpuDisplayState.Unknown,
            "missing NVAPI library never becomes Disconnected");
        {
            var api = new FakeNvapi { ThrowOnEnumerate = true };
            var result = NvidiaDisplayQuery.Query(() => api);
            Check(result.State == DgpuDisplayState.Unknown && result.NativeCode is null &&
                result.Detail!.Contains("EnumPhysicalGPUs") && api.Disposed,
                "managed exceptions retain their failing stage without borrowing a prior NVAPI status=0");
        }

        foreach (int capability in new[] { -1, 0, 1, 2, 3, 100 })
        {
            var backend = new FakeBackend { Capability = capability };
            await using var service = Service(backend, null, new());
            await service.RefreshAsync();
            Check(service.Snapshot.Supported == (capability > 0) && backend.SetCalls == 0,
                "all positive capability values show one card; probe never writes");
            Check(capability > 0 || backend.DisplayCalls == 0, "unsupported capability does not initialize NVAPI");
        }
        {
            var backend = new FakeBackend();
            await using var service = Service(backend, null, new());
            await service.RefreshAsync();
            backend.CapabilityError = new InvalidDataException("class/method/instance/invocation/field failure");
            await service.RefreshAsync();
            Check(!service.Snapshot.Supported && service.CapabilityDiagnostic.Contains("CapabilityProbeFailed"),
                "a fresh capability failure hides a previously visible card instead of retaining cached support");
            backend.CapabilityError = null;
            backend.FailReadAt = backend.ReadCalls + 1;
            await service.RefreshAsync();
            Check(service.Snapshot.Supported && !service.Snapshot.Available,
                "runtime read failure cannot erase a successfully probed capability");
        }
        foreach (var state in new[] { DgpuDisplayState.Connected, DgpuDisplayState.Unknown })
        {
            var backend = new FakeBackend { DisplayState = state };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "Enabled=True");
            Check(result.Outcome == GpuModeOperationOutcome.Blocked && result.Failure?.Stage == GpuModeOperationStage.DisplayGuard &&
                backend.SetCalls == 0 && keeper.Suspends == 0, "Connected/Unknown cannot suspend keep-awake or invoke SET");
            Check(result.Diagnostics.Contains("DisplayGuard") && result.Failure!.Backend == "NVAPI" &&
                result.Failure.NativeCode == (state == DgpuDisplayState.Unknown ? -123 : 0), "blocked operation retains its real NVAPI diagnostic");
        }
        foreach (bool enabled in new[] { false, true })
        {
            var backend = new FakeBackend();
            var keeper = new FakeKeeper { Enabled = enabled };
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "fixture");
            Check(result.Succeeded && result.EffectiveMode == 1 && backend.SetCalls == 1 && keeper.Suspends == 1 &&
                keeper.Suppressed && !keeper.Active && keeper.Enabled == enabled,
                "iGPU-only suspends runtime, SETs once, preserves preference, and does not resume the context");
            Check(backend.Order.SequenceEqual(new[] { "read", "display", "suspend", "set1", "read", "complete1" }),
                "the guarded transition's stage order is deterministic");
        }
        {
            var backend = new FakeBackend();
            var keeper = new FakeKeeper { SuspendFails = true };
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "fixture");
            Check(result.Outcome == GpuModeOperationOutcome.Failed && result.Failure?.Stage == GpuModeOperationStage.KeepAwakeSuspend &&
                backend.SetCalls == 0, "suspend failure stops before WMI SET");
        }
        foreach (bool setThrows in new[] { false, true })
        {
            var backend = new FakeBackend { ApplySet = false, SetError = setThrows ? new IOException("native fixture") : null };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "fixture");
            Check(result.Outcome == GpuModeOperationOutcome.Failed && result.Failure?.Stage ==
                (setThrows ? GpuModeOperationStage.ModeSet : GpuModeOperationStage.EffectiveStateVerify) &&
                keeper.Active && !keeper.Suppressed && backend.SetCalls == 1,
                "SET exception and unchanged readback are distinct, both reconcile keep-awake to prior actual mode");
            Check(result.SetInvoked != setThrows && result.Diagnostics.Contains("outcome=Failed"), "trace records whether SET completed without exception");
        }
        {
            var backend = new FakeBackend { FailReadAt = 2 };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "fixture");
            Check(result.Outcome == GpuModeOperationOutcome.Indeterminate && result.Failure?.Stage == GpuModeOperationStage.ModeReadback &&
                result.SetInvoked && result.EffectiveMode is null && !keeper.Active && keeper.Suppressed,
                "unreadable post-SET state is Indeterminate and cannot restore keep-awake blindly");
            Check(!GpuAutoPolicy.ShouldRetry(result, 1), "indeterminate outcome never silently resends SET");
        }
        {
            var backend = new FakeBackend { FailReadAt = 1 };
            var result = await Transaction(backend, new()).ExecuteAsync(1, 3, "fixture");
            Check(result.Failure?.Stage == GpuModeOperationStage.RuntimeStateRead && backend.SetCalls == 0,
                "pre-SET runtime failure is reported at RuntimeStateRead");
        }
        {
            var backend = new FakeBackend { SetError = new IOException("WMI SET failure") };
            var result = await Transaction(backend, new() { CompleteFails = true }).ExecuteAsync(1, 3, "fixture");
            Check(result.Failure?.Stage == GpuModeOperationStage.ModeSet &&
                result.AuxiliaryFailure?.Stage == GpuModeOperationStage.KeepAwakeRestore && !result.Succeeded,
                "failed SET and failed auxiliary restore retain both independent failure contexts");
        }
        {
            var backend = new FakeBackend();
            var keeper = new FakeKeeper();
            keeper.Order = backend.Order;
            using var cancellation = new CancellationTokenSource();
            var transaction = new GpuModeTransaction(backend, keeper.SuspendAsync, keeper.CompleteAsync,
                () => { cancellation.Cancel(); return Task.CompletedTask; });
            var result = await transaction.ExecuteAsync(1, 3, "fixture", cancellationToken: cancellation.Token);
            Check(result.Succeeded && result.EffectiveMode == 1 && keeper.Suppressed && backend.ReadCalls == 2,
                "post-SET cancellation cannot skip readback or restart a helper on an unverified GPU");
        }
        foreach (int already in new[] { 0, 1 })
        {
            var backend = new FakeBackend { Mode = already, DisplayState = DgpuDisplayState.Unknown };
            var result = await Transaction(backend, new()).ExecuteAsync(already, 3, "fixture");
            Check(result.Succeeded && !result.SetInvoked && backend.SetCalls == 0,
                "already-confirmed target reconciles runtime without redundant SET or a meaningless display query");
        }
        foreach (bool restoreFails in new[] { false, true })
        {
            var backend = new FakeBackend { Mode = 1, DisplayState = DgpuDisplayState.Unknown };
            var keeper = new FakeKeeper { Active = false, Suppressed = true, CompleteFails = restoreFails };
            var result = await Transaction(backend, keeper).ExecuteAsync(0, 3, "fixture");
            Check(result.Succeeded && result.EffectiveMode == 0 && backend.SetCalls == 1 && backend.DisplayCalls == 0 &&
                result.Outcome == (restoreFails ? GpuModeOperationOutcome.SuccessWithWarning : GpuModeOperationOutcome.Success),
                "Default does not require the display guard and auxiliary failures cannot turn core success into failure");
            Check(restoreFails ? result.AuxiliaryFailure?.Stage == GpuModeOperationStage.KeepAwakeRestore : keeper.Active && !keeper.Suppressed,
                "verified Default clears suppression and restores saved preference, or explicitly warns");
        }
        {
            var backend = new FakeBackend { Power = 1 };
            using var cancel = new CancellationTokenSource();
            var keeper = new FakeKeeper { OnSuspend = () => { backend.Power = 0; cancel.Cancel(); } };
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "fixture", 1, cancel.Token);
            Check(result.Outcome == GpuModeOperationOutcome.Blocked && backend.SetCalls == 0 && keeper.Active,
                "AC/cancellation after suspend stops a DC SET and reconciles prior runtime");
        }
        foreach (int power in new[] { 0, 1 })
        foreach (var display in Enum.GetValues<DgpuDisplayState>())
        {
            var backend = new FakeBackend { Mode = power == 0 ? 1 : 0, Power = power, DisplayState = display };
            await using var service = Service(backend, GpuWorkingMode.Auto, new());
            await service.InitializeAsync(() => true);
            Check(backend.SetCalls == (power == 0 || display == DgpuDisplayState.Disconnected ? 1 : 0),
                "Auto AC returns Default; DC only switches after Disconnected");
            Check(service.Snapshot.LogicalMode == GpuWorkingMode.Auto, "Auto remains an application preference, not an OEM register");
        }
        {
            var backend = new FakeBackend { ApplySet = false };
            await using var service = Service(backend, null, new());
            bool failed = false;
            try { await service.SelectAsync(GpuWorkingMode.IGPUOnly); }
            catch (GpuModeOperationException ex) { failed = ex.Result.Failure?.Stage == GpuModeOperationStage.EffectiveStateVerify; }
            await Task.Delay(50);
            Check(failed && backend.SetCalls == 1 && service.Snapshot.LogicalMode == GpuWorkingMode.Default,
                "manual click makes exactly one attempt and does not select an unconfirmed target");
        }
        {
            int queries = 0;
            var backend = new FakeBackend { DisplayProvider = () => ++queries == 1
                ? DgpuDisplayState.Disconnected : DgpuDisplayState.Connected };
            await using var service = Service(backend, null, new());
            try { await service.SelectAsync(GpuWorkingMode.IGPUOnly); } catch (GpuModeOperationException) { }
            Check(backend.SetCalls == 0 && service.LastOperation?.Outcome == GpuModeOperationOutcome.Blocked,
                "a cached Disconnected snapshot cannot override a fresh Connected guard at click time");
        }
        {
            var backend = new FakeBackend();
            var keeper = new FakeKeeper();
            await using var service = Service(backend, null, keeper);
            bool reserved = false;
            service.SuspendKeepAwake = () =>
            {
                reserved = service.StateRevision > service.Snapshot.Revision;
                return keeper.SuspendAsync();
            };
            await service.SelectAsync(GpuWorkingMode.IGPUOnly);
            Check(reserved && service.Snapshot.Revision == service.StateRevision,
                "transition reserves a newer revision before suspend and publishes it only after reconciliation");
        }
        {
            var backend = new FakeBackend { ApplySet = false, Power = 1 };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), TimeSpan.FromMilliseconds(15));
            await service.InitializeAsync(() => true);
            await UntilAsync(() => backend.SetCalls == 6);
            await Task.Delay(80);
            Check(backend.SetCalls == 6 && service.LastOperation?.Failure?.Stage == GpuModeOperationStage.EffectiveStateVerify,
                "Auto has one initial attempt plus at most five retries, preserving final stage");
        }
        foreach (var block in new[] { DgpuDisplayState.Connected, DgpuDisplayState.Unknown })
        {
            var backend = new FakeBackend { ApplySet = false, Power = 1 };
            backend.OnSet = () => backend.DisplayState = block;
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), TimeSpan.FromMilliseconds(25));
            await service.InitializeAsync(() => true);
            await Task.Delay(100);
            Check(backend.SetCalls == 1 && service.LastOperation?.Outcome == GpuModeOperationOutcome.Blocked &&
                service.LastOperation.Failure?.Stage == GpuModeOperationStage.DisplayGuard,
                "new Connected/Unknown state immediately ends DC retries with its own display-guard diagnostic");
        }
        {
            var backend = new FakeBackend { ApplySet = false, Power = 1 };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), TimeSpan.FromMilliseconds(25));
            await service.InitializeAsync(() => true);
            backend.Power = 0;
            await service.HandleSystemChangeAsync();
            await Task.Delay(100);
            Check(backend.SetCalls == 1, "AC event cancels pending DC retries even when Default is already active");
        }
        return checks;
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(10, deadline.Token);
    }

    private static GpuModeTransaction Transaction(FakeBackend backend, FakeKeeper keeper)
    {
        keeper.Order = backend.Order;
        return new(backend, keeper.SuspendAsync, keeper.CompleteAsync, () => Task.CompletedTask);
    }
    private static LenovoGpuWorkingModeService Service(FakeBackend backend, GpuWorkingMode? preference,
        FakeKeeper keeper, TimeSpan? retryDelay = null)
    {
        keeper.Order = backend.Order;
        return new(backend, preference, _ => { }, () => Task.CompletedTask, retryDelay)
        {
            SuspendKeepAwake = keeper.SuspendAsync, CompleteKeepAwake = keeper.CompleteAsync
        };
    }

    private sealed class FakeBackend : IGpuModeBackend
    {
        public int Capability = 3, Mode, Power, SetCalls, ReadCalls, DisplayCalls, FailReadAt;
        public bool ApplySet = true;
        public Exception? CapabilityError, SetError;
        public Action? OnSet;
        public Func<DgpuDisplayState>? DisplayProvider;
        public DgpuDisplayState DisplayState = DgpuDisplayState.Disconnected;
        public List<string> Order = [];
        public long ProbeCapability() => CapabilityError is { } ex ? throw ex : Capability;
        public int ReadMode()
        {
            Order.Add("read");
            if (++ReadCalls == FailReadAt) throw new IOException("readback fixture");
            return Mode;
        }
        public void SetMode(int mode)
        {
            SetCalls++; Order.Add("set" + mode);
            OnSet?.Invoke();
            if (SetError is { } ex) throw ex;
            if (ApplySet) Mode = mode;
        }
        public DgpuDisplayQueryResult QueryDisplays()
        {
            DisplayCalls++; Order.Add("display");
            if (DisplayProvider is not null) DisplayState = DisplayProvider();
            return new(DisplayState, NativeCode: DisplayState == DgpuDisplayState.Unknown ? -123 : 0,
                Detail: "NvAPI_GPU_GetConnectedDisplayIds fixture");
        }
        public int ReadPowerSource() => Power;
        public bool ReadNvidiaPresence() => Mode != 1;
    }

    private sealed class FakeKeeper
    {
        public bool Enabled = true, Active = true, Suppressed, SuspendFails, CompleteFails;
        public int Suspends;
        public Action? OnSuspend;
        public List<string> Order = [];
        public Task SuspendAsync()
        {
            Suspends++; Order.Add("suspend");
            if (SuspendFails) throw new IOException("stop fixture");
            Active = false; OnSuspend?.Invoke();
            return Task.CompletedTask;
        }
        public Task CompleteAsync(int? actual)
        {
            Order.Add("complete" + actual);
            if (CompleteFails) throw new IOException("restore fixture");
            Suppressed = actual != 0; Active = Enabled && !Suppressed;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNvapi : INvidiaDisplayApi
    {
        public int Gpus = 1, ConnectedGpu, FailStage, DataCalls;
        public uint DisplayCount;
        public bool GrowCount, Disposed, ThrowOnEnumerate, VersionsValid = true;
        public int Initialize() => FailStage == 1 ? -123 : 0;
        public int Enumerate(nint[] handles, out int count)
        {
            if (ThrowOnEnumerate) throw new IOException("enumeration fixture");
            count = Gpus;
            for (int i = 0; i < Math.Min(Gpus, handles.Length); i++) handles[i] = i + 1;
            return FailStage == 2 ? -123 : 0;
        }
        public int GetDisplays(nint gpu, NvGpuDisplayId[]? displays, ref uint count)
        {
            if (displays is null)
            {
                count = gpu == ConnectedGpu ? 1 : DisplayCount;
                return FailStage == 3 ? -123 : 0;
            }
            DataCalls++;
            VersionsValid &= displays.All(display => display.StructureVersion == NvGpuDisplayId.Version);
            if (displays.Length > 0 && gpu == ConnectedGpu) displays[0].Flags = 64;
            if (GrowCount) count++;
            return FailStage == 4 ? -123 : 0;
        }
        public void Dispose() => Disposed = true;
    }
}
