namespace YogaITS.Tray.Services;

public sealed record GpuKeepAwakeSnapshot(bool Enabled, bool Active, string Message);

internal static class GpuKeepAwakePolicy
{
    public const string HelperFileName = "YogaITS.dGPUKeepAwake.exe";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    public static bool IsNvidiaHardware(uint vendor, uint flags) => vendor == 0x10DE && (flags & 2) == 0;
    public static bool IsDeviceUsable(bool exists, int removedReason) => exists && removedReason >= 0;
    public static bool ParentMatches(int expectedPid, long expectedTicks, int pid, long ticks, string name, bool exited) =>
        expectedPid > 0 && expectedTicks > 0 && !exited && pid == expectedPid && ticks == expectedTicks &&
        name.Equals("YogaITS.Tray", StringComparison.OrdinalIgnoreCase);

    public static bool MayRun(bool enabled, bool present, bool modeSupported, int effectiveMode,
        int powerSource, bool suspended = false) => enabled && present && powerSource == 0 && !suspended &&
        (!modeSupported || effectiveMode != 1);

    public static bool IsHelperName(string name)
    {
        string file = name.Trim().Trim('"').Replace('\\', '/').Split('/')[^1];
        return file.Equals(HelperFileName, StringComparison.OrdinalIgnoreCase) ||
            file.Equals("YogaITS.dGPUKeepAwake", StringComparison.OrdinalIgnoreCase);
    }
}
