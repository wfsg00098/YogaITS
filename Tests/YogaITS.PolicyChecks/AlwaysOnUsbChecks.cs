using System.ComponentModel;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.AlwaysOnUsb;

internal static class AlwaysOnUsbChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Always-On USB: " + message);
            checks++;
        }
        async Task Reject(Func<Task> operation)
        {
            bool failed = false;
            try { await operation(); }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ArgumentOutOfRangeException)
            { failed = true; }
            Check(failed, "failed transactions must report failure");
        }
        Check(AlwaysOnUsbBackend.Ioctl == 0x831020E8 && AlwaysOnUsbBackend.GetCommand == 2,
            "use the settings IOCTL and read-only support probe, not the keyboard IOCTL");
        foreach (var (raw, expected) in new (uint, AlwaysOnUsbMode)[]
        {
            (0, AlwaysOnUsbMode.Off), (0x8000, AlwaysOnUsbMode.Off),
            (0x80, AlwaysOnUsbMode.SleepOnly), (0x8080, AlwaysOnUsbMode.Always),
            (0x80000000, AlwaysOnUsbMode.Off), (0xFFFFFFFF, AlwaysOnUsbMode.Always),
            (0x12340080, AlwaysOnUsbMode.SleepOnly)
        })
            Check(AlwaysOnUsbBackend.Decode(raw) == expected, "decode little-endian bits without inventing a validity flag");

        foreach (var (mode, commands, raw) in new (AlwaysOnUsbMode, uint[], uint)[]
        {
            (AlwaysOnUsbMode.Off, [0xB, 0x12], 0),
            (AlwaysOnUsbMode.SleepOnly, [0xA, 0x12], 0x80),
            (AlwaysOnUsbMode.Always, [0xA, 0x13], 0x8080)
        })
        {
            Check(AlwaysOnUsbBackend.SetCommands(mode).SequenceEqual(commands), "each BIOS mode has its exact ordered SET pair");
            var fixture = new Fixture([R(0), W(commands[0]), W(commands[1]), R(raw)]);
            await using var service = new AlwaysOnUsbService(fixture.Backend);
            await service.SetAsync(mode);
            Check(service.Snapshot == new AlwaysOnUsbSnapshot(true, true, mode) && fixture.Complete,
                "fresh GET precedes SET and only readback publishes the state; handles are released");
        }

        var invalid = new Fixture([]);
        await Reject(async () => await invalid.Backend.SetAsync((AlwaysOnUsbMode)3));
        Check(invalid.Opens == 0, "invalid options never open or write a device");

        var delayed = new Fixture([R(0), R(0), W(0xA), W(0x13), R(0x80), R(0x8080)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delayed.Delay = _ => { entered.TrySetResult(); return release.Task; };
        await using (var service = new AlwaysOnUsbService(delayed.Backend))
        {
            await service.RefreshAsync();
            Task pending = service.SetAsync(AlwaysOnUsbMode.Always);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(service.Snapshot.Mode == AlwaysOnUsbMode.Off, "pending readback cannot select the requested state");
            }
            finally { release.TrySetResult(); }
            await pending;
            Check(service.Snapshot.Mode == AlwaysOnUsbMode.Always && delayed.Complete,
                "readback retries do not resend either SET");
        }

        var mismatch = new Fixture(new[] { R(0x80), W(0xA), W(0x13) }.Concat(Enumerable.Repeat(R(0x80), 10)).ToArray());
        await using (var service = new AlwaysOnUsbService(mismatch.Backend))
        {
            await Reject(() => service.SetAsync(AlwaysOnUsbMode.Always));
            Check(service.Snapshot == new AlwaysOnUsbSnapshot(true, true, AlwaysOnUsbMode.SleepOnly) && mismatch.Complete,
                "bounded mismatched readback reports failure while retaining the actual BIOS state");
        }

        Step[] transaction = [R(0), W(0xA), W(0x13), R(0x8080)];
        for (int failure = 0; failure < transaction.Length; failure++)
        {
            var steps = transaction[..(failure + 1)];
            steps[^1] = steps[^1] with { Error = new IOException("driver failure") };
            var fixture = new Fixture(steps);
            await using var service = new AlwaysOnUsbService(fixture.Backend);
            await Reject(() => service.SetAsync(AlwaysOnUsbMode.Always));
            Check(!service.Snapshot.Supported && !service.Snapshot.Available && fixture.Complete,
                "failed probe, either SET or GET releases the handle and cannot publish a requested state");
        }

        var external = new Fixture([R(0), R(0x8080), R(0, new IOException("temporary")), R(0x80),
            R(0, new Win32Exception(2)), R(0)]);
        await using (var service = new AlwaysOnUsbService(external.Backend))
        {
            await service.RefreshAsync();
            Check(service.Snapshot.Mode == AlwaysOnUsbMode.Off, "zero is a supported Off response");
            await service.RefreshAsync();
            Check(service.Snapshot.Mode == AlwaysOnUsbMode.Always, "external firmware changes appear without any writes");
            await service.RefreshAsync();
            Check(service.Snapshot.Supported && !service.Snapshot.Available && service.Snapshot.Mode == AlwaysOnUsbMode.Always,
                "transient failures retain known capability and confirmed state but disable actions");
            await service.RefreshAsync();
            Check(service.Snapshot.Available && service.Snapshot.Mode == AlwaysOnUsbMode.SleepOnly, "later GET recovers automatically");
            await service.RefreshAsync();
            Check(!service.Snapshot.Supported, "missing devices hide the feature");
            await service.RefreshAsync();
            Check(service.Snapshot.Available && external.Complete, "a reappearing device is reprobed, without a model whitelist");
        }
        await using (var service = new AlwaysOnUsbService(new(() => throw new Win32Exception(2))))
        {
            await service.RefreshAsync();
            Check(service.Snapshot == AlwaysOnUsbSnapshot.Unavailable, "initial unsupported hardware remains hidden");
        }

        var blocked = new Fixture([R(0), W(0xA), W(0x13), R(0x8080)]);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task battery = BatteryDriverAccess.WriteAsync(async () => { holding.SetResult(); await unblock.Task; return true; });
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Task write = blocked.Backend.SetAsync(AlwaysOnUsbMode.Always);
            Check(blocked.Opens == 0, "USB transactions share the existing battery driver gate");
            unblock.SetResult();
            await Task.WhenAll(battery, write);
            Check(blocked.Complete, "battery and USB operations cannot interleave driver command pairs");
        }
        finally { unblock.TrySetResult(); await battery; }
        return checks;
    }

    private static Step R(uint raw, Exception? error = null) => new(false, 0, raw, error);
    private static Step W(uint command) => new(true, command, 0);
    private sealed record Step(bool Write, uint Command, uint Raw, Exception? Error = null);

    private sealed class Fixture(Step[] steps)
    {
        private readonly Queue<Step> _steps = new(steps);
        private int _closes;
        public int Opens { get; private set; }
        public bool Complete => _steps.Count == 0 && Opens == _closes;
        public Func<TimeSpan, Task> Delay { get; set; } = _ => Task.CompletedTask;
        public AlwaysOnUsbBackend Backend => new(() => { Opens++; return new Device(this); }, duration => Delay(duration));

        private sealed class Device(Fixture fixture) : IAlwaysOnUsbDevice
        {
            public uint Read() => Send(false, 0);
            public void Write(uint command) => _ = Send(true, command);
            private uint Send(bool write, uint command)
            {
                if (!fixture._steps.TryDequeue(out var step) || step.Write != write || step.Command != command)
                    throw new Exception($"Unexpected USB command: write={write}, command=0x{command:X8}");
                if (step.Error is { } error) throw error;
                return step.Raw;
            }
            public void Dispose() => fixture._closes++;
        }
    }
}
