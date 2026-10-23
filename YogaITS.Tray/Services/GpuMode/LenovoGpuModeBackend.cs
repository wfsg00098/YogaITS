using System.Management;

namespace YogaITS.Tray.Services;

internal interface IGpuModeBackend
{
    long ProbeCapability();
    int ReadMode();
    void SetMode(int mode);
    DgpuDisplayQueryResult QueryDisplays();
    int ReadPowerSource();
    bool ReadNvidiaPresence();
}

internal sealed class LenovoGpuModeBackend : IGpuModeBackend
{
    private static ManagementObjectSearcher Search() => new(
        new ManagementScope(@"\\.\root\WMI", new ConnectionOptions
        {
            EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate
        }), new ObjectQuery("SELECT * FROM LENOVO_GAMEZONE_DATA"));

    internal static long DecodeData(object? value) => value switch
    {
        int number => number,
        uint number => number,
        _ => throw new InvalidDataException("Lenovo GPU WMI Data 缺失、类型异常或超出范围。")
    };

    private static long Get(string method)
    {
        using var search = Search();
        using var instances = search.Get();
        foreach (ManagementObject instance in instances)
        {
            using (instance)
            using (var result = instance.InvokeMethod(method, null, null))
                return DecodeData(result?["Data"]);
        }
        throw new InvalidOperationException("未找到 LENOVO_GAMEZONE_DATA 实例。");
    }

    public long ProbeCapability() => Get("IsSupportIGPUMode");
    public int ReadMode()
    {
        long mode = Get("GetIGPUModeStatus");
        return mode is 0 or 1 ? (int)mode : throw new InvalidDataException($"GetIGPUModeStatus 返回未知状态：{mode}。");
    }

    public void SetMode(int mode)
    {
        if (mode is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var search = Search();
        using var instances = search.Get();
        foreach (ManagementObject instance in instances)
        {
            using (instance)
            {
                using var input = instance.GetMethodParameters("SetIGPUModeStatus");
                input["mode"] = (uint)mode;
                using var result = instance.InvokeMethod("SetIGPUModeStatus", input, null);
                return; // Invoke on the instance; no invented ReturnValue contract.
            }
        }
        throw new InvalidOperationException("未找到 LENOVO_GAMEZONE_DATA 实例。");
    }

    public DgpuDisplayQueryResult QueryDisplays() => NvidiaDisplayQuery.Query();
    public int ReadPowerSource() => SystemEventMonitor.ReadPowerSource();
    public bool ReadNvidiaPresence() => NvidiaGpuHelper.HasNvidiaDisplayDevice();
}
