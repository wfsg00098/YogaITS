namespace YogaITS.Tray.Services;

// LLT's EnergyDrv ITS capability checks, separated from hardware I/O for tests.
internal static class ItsCapabilityPolicy
{
    public const uint CurrentModeQuery = 2;
    public const uint ExtendedCapabilitiesQuery = 10;
    // SystemEventMonitor uses 0 = AC, 1 = battery, -1 = unknown.
    public static bool CanSelectFourthMode(int powerSource) => powerSource == 0;

    public static bool SupportsIts(bool querySucceeded, uint currentMode) =>
        querySucceeded && (currentMode & 0x1) != 0;

    public static bool SupportsFourthMode(bool querySucceeded, uint capability, object? versionedState) =>
        querySucceeded && (capability & 0x00020001) == 0x00020001 && versionedState is int;
}
