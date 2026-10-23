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
        {
            var backend = new FakeBackend { Mode = 1, Power = 1, DisplayState = DgpuDisplayState.Unknown };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), observationDelay: TimeSpan.FromMilliseconds(10));
            await service.InitializeAsync(() => true);
            await service.HandleSystemChangeAsync(displayChanged: true);
            await UntilAsync(() => !service.IsObserving);
            Check(backend.SetCalls == 0 && service.Snapshot.EffectiveMode == 1 && service.Snapshot.RawWmiMode == 1 &&
                service.Snapshot.DgpuDisplayState == DgpuDisplayState.Unknown && !service.Snapshot.ModeConflict,
                "confirmed iGPU-only meets DC target even when ejected NVIDIA APIs are unavailable; Unknown is not fabricated as Disconnected");
            Check(service.LastOperation?.Outcome == GpuModeOperationOutcome.Success && !service.Snapshot.Message.Contains("无法"),
                "already-at-target check precedes NVAPI guard and clears spurious Auto blocking");
            Check(backend.ReadCalls == 7 && service.RuntimeDiagnostic.Contains("WMI=1") &&
                service.RuntimeDiagnostic.Contains("NVAPI=Unknown") && service.RuntimeDiagnostic.Contains("time="),
                "one event has exactly five finite read-only follow-ups with timestamped raw diagnostics");
        }
        {
            var backend = new FakeBackend { Power = 1, DisplayState = DgpuDisplayState.Connected };
            backend.DisplayProvider = () => backend.Mode == 1 ? DgpuDisplayState.Unknown : backend.DisplayState;
            var keeper = new FakeKeeper();
            await using var service = Service(backend, GpuWorkingMode.Auto, keeper, observationDelay: TimeSpan.FromMilliseconds(10));
            await service.InitializeAsync(() => true);
            backend.DisplayState = DgpuDisplayState.Disconnected;
            await service.HandleSystemChangeAsync(displayChanged: true);
            await UntilAsync(() => !service.IsObserving);
            Check(backend.SetCalls == 1 && service.Snapshot.EffectiveMode == 1 && keeper.Suppressed && keeper.Enabled,
                "Yoga unplug scenario switches once then accepts post-ejection Unknown without resending SET or restoring helper");
            Check(service.LastOperation?.Succeeded == true && !service.Snapshot.Message.Contains("无法"),
                "read-only follow-up cannot replace successful iGPU switch with a failed guard");
        }
        {
            var backend = new FakeBackend { Mode = 1, Power = 1, DisplayState = DgpuDisplayState.Unknown };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), observationDelay: TimeSpan.FromMilliseconds(10));
            await service.InitializeAsync(() => true);
            backend.DisplayState = DgpuDisplayState.Connected;
            int firstRead = backend.ReadCalls;
            backend.ModeProvider = () => backend.ReadCalls <= firstRead + 2 ? 1 : 0;
            await service.HandleSystemChangeAsync(displayChanged: true);
            Check(service.Snapshot.ModeConflict && service.Snapshot.EffectiveMode == -1 && service.Snapshot.RawWmiMode == 1 &&
                service.Snapshot.ActualMode == 0 && service.Snapshot.Message.Contains("双显") && !service.Snapshot.Message.Contains("待确认"),
                "NVAPI confirms actual dual mode during HDMI firmware wake without rewriting the retained WMI=1");
            await UntilAsync(() => !service.IsObserving);
            Check(service.Snapshot.EffectiveMode == 0 && !service.Snapshot.ModeConflict && backend.SetCalls == 0 &&
                service.Snapshot.LogicalMode == GpuWorkingMode.Auto && service.Snapshot.Message.Contains("双显"),
                "finite observation converges delayed firmware WMI to Default without any SET or preference change");
            Check(service.RuntimeDiagnostic.Contains("WMI=1") && service.RuntimeDiagnostic.Contains("WMI=0"),
                "diagnostics distinguish an old read from a delayed WMI update");
        }
        {
            var backend = new FakeBackend { Mode = 1, Power = 1, DisplayState = DgpuDisplayState.Connected };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), observationDelay: TimeSpan.FromMilliseconds(10));
            await service.InitializeAsync(() => true);
            await UntilAsync(() => !service.IsObserving);
            Check(backend.ReadCalls == 6 && backend.SetCalls == 0 && service.Snapshot.ModeConflict &&
                service.Snapshot.RawWmiMode == 1 && service.Snapshot.EffectiveMode == -1 && service.Snapshot.ActualMode == 0,
                "persistent WMI/display disagreement still shows actual dual mode after bounded observation without any SET");
            Check(service.LastOperation?.Outcome == GpuModeOperationOutcome.Blocked && service.Snapshot.Message.Contains("双显") &&
                !service.Snapshot.Message.Contains("待确认") && service.RuntimeDiagnostic.Contains("actual=0"),
                "a connected output resolves actual display state while the iGPU request remains safely blocked");
        }
        {
            var backend = new FakeBackend { Power = 1, DisplayState = DgpuDisplayState.Unknown };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), observationDelay: TimeSpan.FromMilliseconds(10));
            await service.HandleSystemChangeAsync(displayChanged: true);
            backend.DisplayState = DgpuDisplayState.Disconnected;
            await UntilAsync(() => !service.IsObserving);
            Check(backend.SetCalls == 0 && service.Snapshot.DgpuDisplayState == DgpuDisplayState.Disconnected,
                "observation only reads: recovering NVAPI is not permission for follow-up SETs");
        }
        {
            var backend = new FakeBackend { Mode = 1, Power = 1, DisplayState = DgpuDisplayState.Unknown };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), observationDelay: TimeSpan.FromMilliseconds(30));
            await service.HandleSystemChangeAsync(displayChanged: true);
            await service.SelectAsync(GpuWorkingMode.Default);
            int reads = backend.ReadCalls;
            await Task.Delay(100);
            Check(!service.IsObserving && backend.ReadCalls == reads && service.Snapshot.EffectiveMode == 0,
                "manual selection cancels pending observation so old samples cannot overwrite its result");
        }
        {
            var backend = new FakeBackend { Mode = 1, Power = 1, DisplayState = DgpuDisplayState.Unknown };
            var service = Service(backend, GpuWorkingMode.Auto, new(), observationDelay: TimeSpan.FromMilliseconds(30));
            await service.HandleSystemChangeAsync(displayChanged: true);
            await service.DisposeAsync();
            int reads = backend.ReadCalls;
            await Task.Delay(100);
            Check(!service.IsObserving && backend.ReadCalls == reads, "disposal cancels finite readback observation");
        }
        foreach (var fresh in new[] { DgpuDisplayState.Connected, DgpuDisplayState.Disconnected })
        {
            var backend = new FakeBackend { Mode = 1, DisplayState = fresh };
            var result = await Transaction(backend, new()).ExecuteAsync(1, 3, "fixture",
                observation: new(DgpuDisplayState.Connected));
            Check(backend.SetCalls == 0 && backend.DisplayCalls == 1 && result.Succeeded == (fresh == DgpuDisplayState.Disconnected),
                "known WMI/display disagreement cannot bypass guard via an already-at-target shortcut");
        }
        foreach (var display in Enum.GetValues<DgpuDisplayState>())
        foreach (bool present in new[] { false, true })
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = present, DisplayState = display };
            await using var service = Service(backend, null, new(), observationDelay: TimeSpan.FromMilliseconds(10));
            await service.RefreshAsync();
            bool confirmed = !present && display != DgpuDisplayState.Connected;
            Check((service.Snapshot.EffectiveMode == 1) == confirmed && service.Snapshot.ModeConflict != confirmed &&
                service.Snapshot.Available && backend.SetCalls == 0,
                "WMI=1 alone never confirms ejection: independently observe device presence and display output");
            Check(service.RuntimeDiagnostic.Contains("nvidiaPresent=" + present),
                "diagnostics preserve device presence independently of raw WMI and NVAPI");
            Check(service.Snapshot.ActualMode == (display == DgpuDisplayState.Connected ? 0 : confirmed ? 1 : -1),
                "only a confirmed connected output resolves actual dual mode; present alone never proves it");
        }
        foreach (var display in new[] { DgpuDisplayState.Connected, DgpuDisplayState.Unknown })
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = true, DisplayState = display };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "firmware wake");
            Check(result.Outcome == GpuModeOperationOutcome.Blocked && result.RawWmiMode == 1 &&
                result.EffectiveMode is null && result.NvidiaPresent == true && backend.SetCalls == 0 && keeper.Suspends == 0,
                "a still-present GPU with WMI=1 cannot bypass Connected/Unknown guard or masquerade as completed");
        }
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = true };
            backend.OnSet = () => backend.NvidiaPresent = false;
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "firmware wake");
            Check(result.Succeeded && result.EffectiveMode == 1 && result.NvidiaPresent == false &&
                backend.Order.SequenceEqual(new[] { "read", "display", "suspend", "set1", "read", "complete1" }),
                "after firmware wake, Disconnected permits one guarded SET1 even while WMI already reads 1");
        }
        foreach (int initial in new[] { 0, 1 })
        {
            var backend = new FakeBackend { Mode = initial, NvidiaPresent = true };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "device remains");
            Check(!result.Succeeded && result.Failure?.Stage == GpuModeOperationStage.EffectiveStateVerify &&
                result.RawWmiMode == 1 && result.EffectiveMode is null && result.NvidiaPresent == true &&
                backend.SetCalls == 1 && keeper.Suppressed && !keeper.Active,
                "SET1 followed by WMI=1 is not success while the device remains; ambiguous mode cannot resume keep-awake");
            Check(result.Message.Contains("独显设备仍然存在") && !result.Diagnostics.Contains("set0"),
                "failed ejection is explicit and never silently adds a Default-to-iGPU bounce");
        }
        foreach (int failedPresenceRead in new[] { 1, 2 })
        {
            var backend = new FakeBackend { FailPresenceAt = failedPresenceRead };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "presence failure");
            Check(result.EffectiveMode is null && result.NvidiaPresent is null && backend.SetCalls == failedPresenceRead - 1 &&
                result.Outcome == (failedPresenceRead == 1 ? GpuModeOperationOutcome.Failed : GpuModeOperationOutcome.Indeterminate) &&
                !GpuAutoPolicy.ShouldRetry(result, 1),
                "unreadable device presence never means absent and cannot trigger blind SETs or retries");
            Check(failedPresenceRead == 1 || result.RawWmiMode == 1 && keeper.Suppressed && !keeper.Active,
                "post-SET presence failure retains the actual raw WMI while suppressing unverified keep-awake");
        }
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = true, SetError = new IOException("fixture") };
            var keeper = new FakeKeeper();
            var result = await Transaction(backend, keeper).ExecuteAsync(1, 3, "ambiguous prior mode");
            Check(result.Failure?.Stage == GpuModeOperationStage.ModeSet && result.EffectiveMode is null &&
                keeper.Suppressed && !keeper.Active && backend.SetCalls == 1,
                "SET failure cannot restore keep-awake from an ambiguous pre-switch WMI=1 observation");
        }
        {
            var backend = new FakeBackend { DisplayState = DgpuDisplayState.Connected, NvidiaPresent = true };
            backend.OnSet = () => { backend.NvidiaPresent = false; backend.DisplayState = DgpuDisplayState.Unknown; };
            var keeper = new FakeKeeper();
            await using var service = Service(backend, GpuWorkingMode.Auto, keeper, observationDelay: TimeSpan.FromMilliseconds(10));
            await service.InitializeAsync(() => true);
            backend.Power = 1;
            await service.HandleSystemChangeAsync();
            Check(backend.SetCalls == 0 && service.Snapshot.EffectiveMode == 0, "unplug AC with a connected display preserves dual mode");
            backend.DisplayState = DgpuDisplayState.Disconnected;
            await service.HandleSystemChangeAsync(displayChanged: true);
            await UntilAsync(() => !service.IsObserving);
            Check(backend.SetCalls == 1 && service.Snapshot.EffectiveMode == 1 && !service.Snapshot.NvidiaPresent,
                "first display unplug SETs and confirms ejection");
            backend.NvidiaPresent = true;
            backend.DisplayState = DgpuDisplayState.Connected;
            await service.HandleSystemChangeAsync(displayChanged: true);
            await UntilAsync(() => !service.IsObserving);
            Check(backend.SetCalls == 1 && service.Snapshot.RawWmiMode == 1 && service.Snapshot.ModeConflict &&
                service.Snapshot.NvidiaPresent && service.Snapshot.EffectiveMode == -1 && service.Snapshot.ActualMode == 0 &&
                !service.Snapshot.Message.Contains("待确认"),
                "firmware HDMI wake shows actual dual mode while preserving WMI=1 and the connected-display guard");
            backend.DisplayState = DgpuDisplayState.Disconnected;
            await service.HandleSystemChangeAsync(displayChanged: true);
            await UntilAsync(() => !service.IsObserving);
            Check(backend.SetCalls == 2 && service.Snapshot.RawWmiMode == 1 && service.Snapshot.EffectiveMode == 1 &&
                !service.Snapshot.NvidiaPresent && !service.Snapshot.ModeConflict && service.Snapshot.ActualMode == 1 && keeper.Suppressed && keeper.Enabled &&
                service.Snapshot.LogicalMode == GpuWorkingMode.Auto && !backend.Order.Contains("set0"),
                "second unplug reissues only SET1, confirms ejection, preserves Auto and saved keep-awake preference");
        }
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = true, Power = 1 };
            await using var service = Service(backend, GpuWorkingMode.Auto, new(), TimeSpan.FromMilliseconds(15));
            await service.InitializeAsync(() => true);
            await UntilAsync(() => backend.SetCalls == 6);
            await Task.Delay(80);
            Check(backend.SetCalls == 6 && service.Snapshot.ModeConflict && service.Snapshot.EffectiveMode == -1 &&
                service.Snapshot.NvidiaPresent && service.LastOperation?.Failure?.Stage == GpuModeOperationStage.EffectiveStateVerify,
                "device-still-present failure with WMI=1 uses bounded Auto retries and cannot terminate them as already satisfied");
        }
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = true, DisplayState = DgpuDisplayState.Connected };
            await using var service = Service(backend, GpuWorkingMode.Auto, new());
            await service.InitializeAsync(() => true);
            Check(backend.SetCalls == 1 && backend.Order.Contains("set0") && service.Snapshot.RawWmiMode == 0 &&
                service.Snapshot.ActualMode == 0 && service.Snapshot.EffectiveMode == 0,
                "AC Auto still explicitly SETs and confirms WMI=0; external output alone cannot satisfy the policy setting");
        }
        {
            var backend = new FakeBackend { Mode = 1, NvidiaPresent = true, DisplayState = DgpuDisplayState.Connected, ApplySet = false };
            await using var service = Service(backend, GpuWorkingMode.Default, new());
            bool failed = false;
            try { await service.SelectAsync(GpuWorkingMode.Default); }
            catch (GpuModeOperationException) { failed = true; }
            Check(failed && backend.SetCalls == 1 && backend.Order.Contains("set0") && service.Snapshot.RawWmiMode == 1 &&
                service.Snapshot.ActualMode == 0 && service.LastOperation?.Succeeded == false,
                "actual dual output cannot falsely verify a manual SET0 whose WMI acknowledgement remains 1");
        }
        {
            var state = new GpuWorkingSnapshot(true, GpuWorkingMode.Auto, -1, DgpuDisplayState.Connected,
                1, true, "", Available: false, RawWmiMode: 1);
            Check(state.ActualMode == -1, "unavailable runtime cannot reuse an old connected output to claim a current actual mode");
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
        FakeKeeper keeper, TimeSpan? retryDelay = null, TimeSpan? observationDelay = null)
    {
        keeper.Order = backend.Order;
        return new(backend, preference, _ => { }, () => Task.CompletedTask, retryDelay, observationDelay)
        {
            SuspendKeepAwake = keeper.SuspendAsync, CompleteKeepAwake = keeper.CompleteAsync
        };
    }

    private sealed class FakeBackend : IGpuModeBackend
    {
        public int Capability = 3, Mode, Power, SetCalls, ReadCalls, DisplayCalls, FailReadAt, PresenceCalls, FailPresenceAt;
        public bool ApplySet = true;
        public bool? NvidiaPresent;
        public Exception? CapabilityError, SetError;
        public Action? OnSet;
        public Func<DgpuDisplayState>? DisplayProvider;
        public Func<int>? ModeProvider;
        public DgpuDisplayState DisplayState = DgpuDisplayState.Disconnected;
        public List<string> Order = [];
        public long ProbeCapability() => CapabilityError is { } ex ? throw ex : Capability;
        public int ReadMode()
        {
            Order.Add("read");
            if (++ReadCalls == FailReadAt) throw new IOException("readback fixture");
            if (ModeProvider is not null) Mode = ModeProvider();
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
        public bool ReadNvidiaPresence()
        {
            if (++PresenceCalls == FailPresenceAt) throw new IOException("presence fixture");
            return NvidiaPresent ?? Mode != 1;
        }
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
