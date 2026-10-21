namespace YogaITS.Tray.Services.KeyboardBacklight;

internal interface IKeyboardBacklightDevice : IDisposable
{
    uint Read(uint command);
    void Write(uint command);
}

// One shared handle per transaction; never try writes to discover capabilities.
internal sealed class KeyboardBacklightBackend
{
    private readonly Func<IKeyboardBacklightDevice> _open;

    public KeyboardBacklightBackend(Func<IKeyboardBacklightDevice>? open = null) =>
        _open = open ?? EnergyDriverKeyboardBacklightDevice.Open;

    public KeyboardBacklightSnapshot Read()
    {
        using var device = _open();
        return Read(device);
    }

    private static KeyboardBacklightSnapshot Read(IKeyboardBacklightDevice device)
    {
        int levels = KeyboardBacklightPolicy.DecodeLevels(device.Read(KeyboardBacklightPolicy.CapabilityCommand));
        int state = KeyboardBacklightPolicy.DecodeState(levels, device.Read(KeyboardBacklightPolicy.GetCommand(levels)));
        return new(levels, state);
    }

    public KeyboardBacklightSnapshot Set(int state)
    {
        using var device = _open();
        // Re-probe capability AND its GET before every write, even after Fn+Space.
        var current = Read(device);
        uint command = KeyboardBacklightPolicy.SetCommand(current.Levels, state);
        device.Write(command);
        int actual = KeyboardBacklightPolicy.DecodeState(current.Levels,
            device.Read(KeyboardBacklightPolicy.GetCommand(current.Levels)));
        if (actual != state)
            throw new InvalidOperationException("键盘背光设置未获回读确认，请重新读取状态后再试。");
        return new(current.Levels, actual);
    }
}
