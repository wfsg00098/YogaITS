namespace YogaITS.Tray.Services.KeyboardBacklight;

internal sealed record KeyboardBacklightSnapshot(int Levels, int State)
{
    public static KeyboardBacklightSnapshot Unavailable { get; } = new(0, -1);
    public bool Supported => Levels is >= 1 and <= 3 && State >= 0 && State <= Levels;
}

internal static class KeyboardBacklightPolicy
{
    public const uint Ioctl = 0x83102144;
    public const uint CapabilityCommand = 0x01;

    public static int DecodeLevels(uint raw)
    {
        uint levels = raw >> 1;
        if ((raw & 1) == 0 || levels is < 1 or > 3)
            throw new InvalidDataException("键盘背光能力无效或协议暂不支持。");
        return (int)levels;
    }

    public static uint GetCommand(int levels) => levels switch
    {
        1 => 0x12,
        2 => 0x22,
        3 => 0x32,
        _ => throw new ArgumentOutOfRangeException(nameof(levels))
    };

    public static uint SetCommand(int levels, int state)
    {
        uint get = GetCommand(levels);
        if (state < 0 || state > levels) throw new ArgumentOutOfRangeException(nameof(state));
        return ((uint)state << 16) | (get + 1);
    }

    public static int DecodeState(int levels, uint raw)
    {
        _ = GetCommand(levels);
        if ((raw & 1) == 0) throw new InvalidDataException("键盘背光状态无效。");
        uint value = raw >> 1;
        if (levels == 3)
        {
            if ((value & 0x8000) == 0)
                throw new InvalidDataException("键盘背光扩展状态标志无效。");
            value &= 0x7FFF;
        }
        if (value > levels) throw new InvalidDataException("键盘背光返回了能力范围外的状态。");
        return (int)value;
    }
}
