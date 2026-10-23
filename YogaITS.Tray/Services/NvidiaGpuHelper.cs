using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace YogaITS.Tray.Services;

public sealed record GpuProcessInfo(int Pid, string Name, string? ExecutablePath = null, long? StartedUtcTicks = null);

public static class NvidiaGpuHelper
{
    internal static readonly object NvapiGate = new();
    private const uint NvapiInitializeId = 0x0150E828;
    private const uint NvapiUnloadId = 0xD22BDD7E;
    private const uint NvapiEnumPhysicalGpusId = 0xE5AC921F;
    private const uint NvapiGpuQueryActiveAppsId = 0x65B1C5F5;

    private const int NvapiOk = 0;
    private const int NvapiMaxPhysicalGpus = 64;
    private const int NvapiMaxApplications = 128;
    private const int NvapiLongStringMax = 256;

    // NV_GPU_PRIVATE_ACTIVE_APPLICATION_V2:
    // uint version + uint pid + char name[256]
    private const int ActiveAppV2Size = 264;
    private const uint ActiveAppV2Version = ActiveAppV2Size | (2u << 16);

    // Match LLT's batch GPU process list, not its individual force-kill menu.
    private static readonly HashSet<string> ExcludedProcessNames =
        new(StringComparer.OrdinalIgnoreCase) { "dwm.exe", "explorer.exe" };

    public static bool HasNvidiaDisplayDevice()
    {
        // Presence detection is intentionally PnP/WMI-only so merely opening the
        // tray menu does not initialize NVAPI just to decide whether to show the
        // NVIDIA actions. The actions themselves still use NVAPI exactly where
        // needed.
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_VideoController");
        using var devices = searcher.Get();
        foreach (ManagementObject device in devices)
        {
            using (device)
            {
                string name = Convert.ToString(device["Name"]) ?? "";
                string id = Convert.ToString(device["PNPDeviceID"]) ?? "";
                if (id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static IReadOnlyList<GpuProcessInfo> QueryActiveApplications()
    {
        lock (NvapiGate) return QueryActiveApplicationsCore();
    }

    private static IReadOnlyList<GpuProcessInfo> QueryActiveApplicationsCore()
    {
        nint library = 0;
        NvNoArg? unload = null;

        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out library))
                throw new InvalidOperationException("未找到 nvapi64.dll。请确认 NVIDIA 驱动已正常安装。");

            nint queryInterfacePtr = NativeLibrary.GetExport(library, "nvapi_QueryInterface");
            var queryInterface = Marshal.GetDelegateForFunctionPointer<NvQueryInterface>(queryInterfacePtr);

            var initialize = GetDelegate<NvNoArg>(queryInterface, NvapiInitializeId);
            unload = GetDelegate<NvNoArg>(queryInterface, NvapiUnloadId);
            var enumPhysicalGpus = GetDelegate<NvEnumPhysicalGpus>(queryInterface, NvapiEnumPhysicalGpusId);
            var queryActiveApps = GetDelegate<NvQueryActiveApps>(queryInterface, NvapiGpuQueryActiveAppsId);

            Check(initialize(), "NvAPI_Initialize");

            var handles = new nint[NvapiMaxPhysicalGpus];
            Check(enumPhysicalGpus(handles, out int gpuCount), "NvAPI_EnumPhysicalGPUs");

            var result = new Dictionary<int, GpuProcessInfo>();

            for (int gpuIndex = 0; gpuIndex < gpuCount; gpuIndex++)
            {
                nint buffer = Marshal.AllocHGlobal(ActiveAppV2Size * NvapiMaxApplications);

                try
                {
                    // NVAPI expects the version field initialized in every slot.
                    for (int i = 0; i < NvapiMaxApplications; i++)
                    {
                        nint item = buffer + i * ActiveAppV2Size;
                        Marshal.WriteInt32(item, 0, unchecked((int)ActiveAppV2Version));
                        Marshal.WriteInt32(item, 4, 0);
                        for (int j = 8; j < ActiveAppV2Size; j++)
                            Marshal.WriteByte(item, j, 0);
                    }

                    uint count = NvapiMaxApplications;
                    int status = queryActiveApps(handles[gpuIndex], buffer, ref count);

                    // Like LLT's QueryActiveApps, a failed query is not an empty list.
                    Check(status, "NvAPI_GPU_QueryActiveApps");

                    int safeCount = Math.Min((int)count, NvapiMaxApplications);

                    for (int i = 0; i < safeCount; i++)
                    {
                        nint item = buffer + i * ActiveAppV2Size;
                        int pid = Marshal.ReadInt32(item, 4);
                        if (pid <= 0 || pid == Environment.ProcessId)
                            continue;

                        byte[] nameBytes = new byte[NvapiLongStringMax];
                        Marshal.Copy(item + 8, nameBytes, 0, nameBytes.Length);
                        int zero = Array.IndexOf(nameBytes, (byte)0);
                        string name = Encoding.UTF8.GetString(
                            nameBytes,
                            0,
                            zero >= 0 ? zero : nameBytes.Length).Trim();

                        string? path = null;
                        long? started = null;
                        try
                        {
                            using Process p = Process.GetProcessById(pid);
                            started = p.StartTime.ToUniversalTime().Ticks;
                            name = string.IsNullOrWhiteSpace(name) ? p.ProcessName : name;
                            path = p.MainModule?.FileName;
                        }
                        catch
                        {
                            // Process may be protected or may have exited.
                        }

                        result[pid] = new(pid, string.IsNullOrWhiteSpace(name) ? $"PID {pid}" : name, path, started);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return result.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            try { unload?.Invoke(); } catch { }
            if (library != 0)
                NativeLibrary.Free(library);
        }
    }

    public static IReadOnlyList<GpuProcessInfo> GetKillCandidates(IEnumerable<GpuProcessInfo> apps)
    {
        // Retain LLT's desktop-process exclusions plus our dedicated keep-awake
        // helper. NVIDIA containers and Session 0/SYSTEM processes remain targets.
        return apps.Where(app => app.Pid > 0 && app.Pid != Environment.ProcessId)
            .Where(app => !ExcludedProcessNames.Contains(app.Name) && !GpuKeepAwakePolicy.IsHelperName(app.Name) &&
                !GpuKeepAwakePolicy.IsHelperName(app.ExecutablePath ?? ""))
            .DistinctBy(app => app.Pid).ToArray();
    }

    public static async Task<GpuActionResult> KillProcessesAsync(IEnumerable<int> pids)
    {
        var targets = new List<GpuProcessInfo>();
        foreach (int pid in pids.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                targets.Add(new(pid, process.ProcessName, StartedUtcTicks: process.StartTime.ToUniversalTime().Ticks));
            }
            catch (ArgumentException) { }
        }
        return await KillProcessesAsync(targets);
    }

    public static bool IsProtectedProcessName(string name) =>
        GpuKeepAwakePolicy.IsHelperName(name) || ExcludedProcessNames.Contains(
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");

    internal static bool SameProcess(long? expectedStart, long actualStart) =>
        expectedStart is > 0 && expectedStart == actualStart;

    public static async Task<GpuActionResult> KillProcessesAsync(IEnumerable<GpuProcessInfo> targets)
    {
        var failed = new List<string>();
        int killed = 0;

        foreach (var target in GetKillCandidates(targets))
        {
            int pid = target.Pid;
            if (pid <= 0 || pid == Environment.ProcessId)
                continue;

            try
            {
                using Process p = Process.GetProcessById(pid);

                if (IsProtectedProcessName(p.ProcessName)) continue;
                if (!SameProcess(target.StartedUtcTicks, p.StartTime.ToUniversalTime().Ticks))
                {
                    failed.Add($"PID {pid}：无法确认进程身份或 PID 已被复用，已跳过。");
                    continue;
                }

                // LLT terminates the process tree and waits for the root to exit.
                // Do not toggle the process-wide debug privilege between targets.
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                killed++;
            }
            catch (ArgumentException)
            {
                // Already exited; that's effectively fine for the goal.
            }
            catch (Exception ex)
            {
                failed.Add($"PID {pid}：{ex.Message}");
            }
        }

        string message = $"已结束 {killed} 个独显占用进程。";
        if (failed.Count > 0)
            message += "\n\n未处理：\n" + string.Join("\n", failed);

        return new(failed.Count == 0, message);
    }

    private static T GetDelegate<T>(NvQueryInterface queryInterface, uint id) where T : Delegate
    {
        nint ptr = queryInterface(id);
        if (ptr == 0)
            throw new InvalidOperationException($"NVAPI function 0x{id:X8} 不可用。");

        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static void Check(int status, string operation)
    {
        if (status != NvapiOk)
            throw new InvalidOperationException($"{operation} 失败，NVAPI status = {status}。");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NvQueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvNoArg();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumPhysicalGpus(
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = NvapiMaxPhysicalGpus)] nint[] handles,
        out int gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvQueryActiveApps(nint gpu, nint applications, ref uint numberOfApplications);
}
