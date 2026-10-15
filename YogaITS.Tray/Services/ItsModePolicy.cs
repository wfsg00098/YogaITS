namespace YogaITS.Tray.Services;

public enum ItsMode
{
    Intelligent,
    Cool,
    Performance,
    Creator
}

internal static class ItsModePolicy
{
    public const int DispatcherVersion3 = 8192;
    public const uint LegacyGeekDisabled = 0x000F100B;
    public const uint LegacyGeekEnabled = 0x001F100B;

    public static bool UsesLegacyService(int dispatcherVersion) => dispatcherVersion < DispatcherVersion3;

    public static ItsMode? FromCurrentSetting(int state) => state switch
    {
        0 => ItsMode.Intelligent,
        1 => ItsMode.Cool,
        3 => ItsMode.Performance,
        4 => ItsMode.Creator,
        _ => null
    };

    public static ItsMode? FromLegacySettings(int automatic, int current, bool geekActive) => (automatic, current) switch
    {
        (2, 0) => ItsMode.Intelligent,
        (1, 1) => ItsMode.Cool,
        (1, 3) => geekActive ? ItsMode.Creator : ItsMode.Performance,
        _ => null
    };

    public static int[] ServiceCommands(bool legacy, ItsMode mode) => (legacy, mode) switch
    {
        (true, ItsMode.Intelligent) => [135],
        (true, ItsMode.Cool) => [134, 146],
        (true, ItsMode.Performance or ItsMode.Creator) => [134, 148],
        (false, ItsMode.Intelligent) => [163],
        (false, ItsMode.Cool) => [164],
        (false, ItsMode.Performance) => [165],
        (false, ItsMode.Creator) => [172],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
