using Microsoft.Win32.SafeHandles;

namespace YogaITS.Tray.Services.AlwaysOnUsb;

internal enum AlwaysOnUsbMode { Off, SleepOnly, Always }

internal sealed record AlwaysOnUsbSnapshot(bool Supported, bool Available, AlwaysOnUsbMode? Mode)
{
    public static AlwaysOnUsbSnapshot Unavailable { get; } = new(false, false, null);
}

internal interface IAlwaysOnUsbDevice : IDisposable
{
    uint Read();
    void Write(uint command);
}

internal sealed class EnergyDriverAlwaysOnUsbDevice : IAlwaysOnUsbDevice
{
    private readonly SafeFileHandle _handle = EnergyDriver.Open();
    public uint Read() => EnergyDriver.SendCode(_handle, AlwaysOnUsbBackend.Ioctl, AlwaysOnUsbBackend.GetCommand);
    public void Write(uint command) => _ = EnergyDriver.SendCode(_handle, AlwaysOnUsbBackend.Ioctl, command);
    public void Dispose() => _handle.Dispose();
}

// Protocol facts checked against LLT master 0bb045be / dev 5db70ed2.
// GET success discovers support; do not import keyboard/battery validity bits.
internal sealed class AlwaysOnUsbBackend
{
    public const uint Ioctl = 0x831020E8;
    public const uint GetCommand = 0x02;
    private readonly Func<IAlwaysOnUsbDevice> _open;
    private readonly Func<TimeSpan, Task> _delay;

    public AlwaysOnUsbBackend(Func<IAlwaysOnUsbDevice>? open = null, Func<TimeSpan, Task>? delay = null)
    {
        _open = open ?? (() => new EnergyDriverAlwaysOnUsbDevice());
        _delay = delay ?? (duration => Task.Delay(duration));
    }

    public static AlwaysOnUsbMode Decode(uint raw)
    {
        // Raw little-endian bit7/bit15 equal bit31/bit23 after LLT's byte swap.
        if ((raw & 0x80) == 0) return AlwaysOnUsbMode.Off;
        return (raw & 0x8000) == 0 ? AlwaysOnUsbMode.SleepOnly : AlwaysOnUsbMode.Always;
    }

    public static uint[] SetCommands(AlwaysOnUsbMode mode) => mode switch
    {
        AlwaysOnUsbMode.Off => [0x0B, 0x12],
        AlwaysOnUsbMode.SleepOnly => [0x0A, 0x12],
        AlwaysOnUsbMode.Always => [0x0A, 0x13],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public AlwaysOnUsbMode Read() => BatteryDriverAccess.Read(() =>
    {
        using var device = _open();
        return Decode(device.Read());
    });

    public Task<(bool Confirmed, AlwaysOnUsbMode Actual)> SetAsync(AlwaysOnUsbMode mode)
    {
        uint[] commands = SetCommands(mode);
        return BatteryDriverAccess.WriteAsync(async () =>
        {
            using var device = _open();
            _ = Decode(device.Read()); // Recheck GET before either SET; never probe by writing.
            foreach (uint command in commands) device.Write(command);
            AlwaysOnUsbMode actual = default;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                actual = Decode(device.Read());
                if (actual == mode) return (true, actual);
                if (attempt < 9) await _delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
            return (false, actual);
        });
    }
}
