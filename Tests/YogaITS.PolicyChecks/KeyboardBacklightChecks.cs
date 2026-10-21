using YogaITS.Tray.Services.KeyboardBacklight;
using YogaITS.Tray.UI;

internal static class KeyboardBacklightChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAILED keyboard backlight check: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IOException or InvalidOperationException)
            { rejected = true; }
            Check(rejected, message);
        }

        Check(KeyboardBacklightPolicy.Ioctl == 0x83102144 && KeyboardBacklightPolicy.CapabilityCommand == 1,
            "all commands use the single EnergyDrv IOCTL and live capability query");
        byte[] payload = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x10007);
        foreach (uint length in new uint[] { 4, 8, 16 })
            Check(EnergyDriverKeyboardBacklightDevice.ReadPayload(payload, length) == 0x10007,
                "GET decodes the first little-endian DWORD from a sufficiently sized response");
        foreach (uint length in new uint[] { 0, 1, 2, 3, 17, uint.MaxValue })
            Reject(() => EnergyDriverKeyboardBacklightDevice.ReadPayload(payload, length), "truncated or impossible driver responses are rejected");
        Reject(() => EnergyDriverKeyboardBacklightDevice.ReadPayload(new byte[3], 4), "short buffers cannot masquerade as a DWORD");
        foreach (uint raw in new uint[] { 0, 1, 2, 4, 6, 8, 9, 0x10007, uint.MaxValue })
            Reject(() => KeyboardBacklightPolicy.DecodeLevels(raw), "invalid or unknown capability cannot choose a fallback protocol");
        foreach (int levels in new[] { -1, 0, 4, int.MaxValue })
            Reject(() => KeyboardBacklightPolicy.GetCommand(levels), "only the three declared protocol families are used");
        Check(!KeyboardBacklightSnapshot.Unavailable.Supported, "unavailable does not select Off or expose controls");
        for (int levels = 1; levels <= 3; levels++)
        {
            uint capability = ((uint)levels << 1) | 1;
            uint get = (uint)(levels * 0x10 + 2);
            Check(KeyboardBacklightPolicy.DecodeLevels(capability) == levels && KeyboardBacklightPolicy.GetCommand(levels) == get,
                "live levels select GET 12/22/32");
            foreach (int invalid in new[] { -1, levels + 1, int.MaxValue })
                Reject(() => KeyboardBacklightPolicy.SetCommand(levels, invalid), "out-of-capability states cannot form SET commands");
            Reject(() => KeyboardBacklightPolicy.DecodeState(levels, 0), "GET must report bit0 valid");
            Reject(() => KeyboardBacklightPolicy.DecodeState(levels, Raw(levels, levels + 1)), "out-of-range GET is not a UI state");
            if (levels == 3)
                foreach (uint noFlag in new uint[] { 1, 3, 5, 7 })
                    Reject(() => KeyboardBacklightPolicy.DecodeState(levels, noFlag), "four-state GET requires its extended flag");

            for (int state = 0; state <= levels; state++)
            {
                uint raw = Raw(levels, state);
                uint set = ((uint)state << 16) | (get + 1);
                Check(KeyboardBacklightPolicy.DecodeState(levels, raw) == state &&
                    KeyboardBacklightPolicy.SetCommand(levels, state) == set,
                    "state decoding and SET payload match the live protocol family");
                if (levels == 3)
                    Check(raw == 0x00010001 + state * 2, "83SF four-state GET values match verified fixtures");
                var fixture = new Fixture([Read(1, capability), Read(get, raw)]);
                var actual = fixture.Backend.Read();
                Check(actual == new KeyboardBacklightSnapshot(levels, state) && actual.Supported && fixture.Complete,
                    "Probe/Get is read-only and releases the device handle");
                fixture = new Fixture([Read(1, capability), Read(get, Raw(levels, 0)), Write(set), Read(get, raw)]);
                Check(fixture.Backend.Set(state) == new KeyboardBacklightSnapshot(levels, state) && fixture.Complete,
                    "SET requires current capability and GET, immediately reads back, and releases the handle");
            }
        }
        Check(SettingsOptionText.KeyboardBacklightLabel(1, 1) == "开启" &&
            SettingsOptionText.KeyboardBacklightLabel(2, 1) == "低亮度" &&
            SettingsOptionText.KeyboardBacklightLabel(3, 3) == "自动",
            "one-level hardware does not get a Dim label or a synthetic Auto state");
        Check(SettingsOptionText.KeyboardBacklightStates(3).SequenceEqual(new[] { 0, 3, 1, 2 }),
            "four-state UI orders Off, Auto, Dim, On without changing firmware encodings");
        Check(SettingsOptionText.KeyboardBacklightStates(1).SequenceEqual(new[] { 0, 1 }) &&
            SettingsOptionText.KeyboardBacklightStates(2).SequenceEqual(new[] { 0, 1, 2 }) &&
            SettingsOptionText.KeyboardBacklightStates(0).Length == 0,
            "hardware without Auto keeps only its capability-supported states");

        var unsupported = new Fixture([Read(1, 1)]);
        Reject(() => unsupported.Backend.Set(0), "unsupported capability blocks SET before any GET or write");
        Check(unsupported.Complete, "unsupported capability stops without trying another protocol or leaking the handle");
        foreach (uint invalidGet in new uint[] { 0, 1, 3, 5, 7, 0x10009 })
        {
            var fixture = new Fixture([Read(1, 7), Read(0x32, invalidGet)]);
            Reject(() => fixture.Backend.Set(1), "invalid GET blocks SET even with a valid capability");
            Check(fixture.Complete, "invalid GET is not followed by a write");
        }
        var outOfRange = new Fixture([Read(1, 3), Read(0x12, 1)]);
        Reject(() => outOfRange.Backend.Set(3), "fresh two-state capability rejects a stale Auto click");
        Check(outOfRange.Complete, "state validation happens before a SET command");

        // Model Fn+Space changes using different live reads, not a stored desired state.
        var sequence = new Fixture([
            Read(1, 7), Read(0x32, 0x10001),
            Read(1, 7), Read(0x32, 0x10005),
            Read(1, 7), Read(0x32, 0x10005), Write(0x10033), Read(0x32, 0x10003),
            Read(1, 7), Read(0x32, 0x10003), Write(0x30033), Read(0x32, 0x10003),
            Read(1, 7), Read(0x32, 0x10003),
            Read(1, 0),
            Read(1, 5), Read(0x22, 5),
            Read(1, 3), Read(0x12, 3), Write(0x13), Read(0x12, 1)
        ]);
        await using (var service = new KeyboardBacklightService(sequence.Backend))
        {
            int changes = 0;
            service.Changed += () => changes++;
            await service.RefreshAsync();
            Check(service.Snapshot == new KeyboardBacklightSnapshot(3, 0), "initial state is read from firmware");
            await service.RefreshAsync();
            Check(service.Snapshot.State == 2, "subsequent refresh sees Fn+Space without writing or enforcing the prior state");
            await service.SetAsync(1);
            Check(service.Snapshot.State == 1 && changes == 3, "only a successful readback publishes the requested selection");
            bool failed = false;
            try { await service.SetAsync(3); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && !service.Snapshot.Supported && service.Snapshot.State != 3,
                "mismatched readback does not select target and hides the whole feature");
            await service.RefreshAsync();
            Check(service.Snapshot.State == 1, "fresh read restores actual firmware state after a failed SET");
            await service.RefreshAsync();
            Check(!service.Snapshot.Supported, "unsupported response clears prior capability rather than retaining stale controls");
            await service.RefreshAsync();
            Check(service.Snapshot == new KeyboardBacklightSnapshot(2, 2), "re-probe dynamically adapts to a different family without restart");
            await service.SetAsync(0);
            Check(service.Snapshot == new KeyboardBacklightSnapshot(1, 0), "SET uses a freshly probed family rather than the last UI snapshot's family");
        }
        Check(sequence.Complete && sequence.Opens == 8, "each transaction owns and disposes one device handle");

        foreach (uint invalidReadback in new uint[] { 0, 7, 0x10009 })
        {
            var fixture = new Fixture([Read(1, 7), Read(0x32, 0x10001), Write(0x30033), Read(0x32, invalidReadback)]);
            await using var service = new KeyboardBacklightService(fixture.Backend);
            bool failed = false;
            try { await service.SetAsync(3); }
            catch (InvalidDataException) { failed = true; }
            Check(failed && !service.Snapshot.Supported && fixture.Complete,
                "invalid SET readback hides the feature without accepting the target or resending commands");
        }

        foreach (int failedStep in new[] { 0, 1, 2, 3 })
        {
            Step[] steps = [Read(1, 7), Read(0x32, 0x10001), Write(0x20033), Read(0x32, 0x10005)];
            steps[failedStep] = steps[failedStep] with { Error = new IOException("simulated driver failure") };
            var fixture = new Fixture(steps[..(failedStep + 1)]);
            await using var service = new KeyboardBacklightService(fixture.Backend);
            bool failed = false;
            try { await service.SetAsync(2); }
            catch (IOException) { failed = true; }
            Check(failed && !service.Snapshot.Supported && fixture.Complete,
                "capability/GET/SET/readback transport failures hide feature, release handle, and never retry writes");
        }
        await using (var service = new KeyboardBacklightService(new KeyboardBacklightBackend(() => throw new IOException("open failed"))))
        {
            await service.RefreshAsync();
            Check(!service.Snapshot.Supported, "missing EnergyDrv is silent and cannot expose controls");
        }
        return checks;
    }

    private static uint Raw(int levels, int state) => ((uint)state << 1) | 1u | (levels == 3 ? 0x10000u : 0);
    private static Step Read(uint command, uint raw) => new(false, command, raw);
    private static Step Write(uint command) => new(true, command, 0);
    private sealed record Step(bool Write, uint Command, uint Raw, Exception? Error = null);

    private sealed class Fixture
    {
        private readonly Queue<Step> _steps;
        public int Opens { get; private set; }
        private int _closes;
        public bool Complete => _steps.Count == 0 && Opens == _closes;
        public KeyboardBacklightBackend Backend { get; }

        public Fixture(Step[] steps)
        {
            _steps = new(steps);
            Backend = new(() => { Opens++; return new Device(this); });
        }

        private sealed class Device(Fixture fixture) : IKeyboardBacklightDevice
        {
            private bool _disposed;
            public uint Read(uint command) => Send(false, command);
            public void Write(uint command) => _ = Send(true, command);
            private uint Send(bool write, uint command)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Device));
                if (!fixture._steps.TryDequeue(out var step) || step.Write != write || step.Command != command)
                    throw new Exception($"Unexpected keyboard command: write={write}, command=0x{command:X8}");
                if (step.Error is { } error) throw error;
                return step.Raw;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                fixture._closes++;
            }
        }
    }
}
