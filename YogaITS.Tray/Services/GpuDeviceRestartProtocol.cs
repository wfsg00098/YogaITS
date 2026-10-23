using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

internal sealed record GpuDeviceRestartRequest(int ParentId, long ParentStartedUtcTicks, string ReadyEvent,
    bool ControlCenter, bool MiniWindow)
{
    public string[] Arguments => [ParentId.ToString(CultureInfo.InvariantCulture), ParentStartedUtcTicks.ToString(CultureInfo.InvariantCulture),
        ReadyEvent, ControlCenter ? "1" : "0", MiniWindow ? "1" : "0"];
}

internal static class GpuDeviceRestartProtocol
{
    public const string HelperName = "YogaITS.dGPURestart.exe";
    public const string MutexName = @"Local\YogaITS.Tray.GpuDeviceRestart";
    public const string ReadyPrefix = @"Local\YogaITS.dGPURestart.";

    public static GpuDeviceRestartRequest? Parse(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0 ||
            !args[2].StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[2][ReadyPrefix.Length..], "N", out _) ||
            args[3] is not ("0" or "1") || args[4] is not ("0" or "1")) return null;
        return new(pid, ticks, args[2], args[3] == "1", args[4] == "1");
    }

    public static bool ParentMatches(GpuDeviceRestartRequest request, int pid, long ticks, string name,
        string? executable, string expectedExecutable, bool exited) => !exited && pid == request.ParentId &&
        ticks == request.ParentStartedUtcTicks && name.Equals("YogaITS.Tray", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(executable, expectedExecutable, StringComparison.OrdinalIgnoreCase);

    public static Process? OpenParent(GpuDeviceRestartRequest request, string executable)
    {
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(request.ParentId);
            // Retain this handle so PID reuse after readiness cannot change ownership.
            _ = parent.Handle;
            if (ParentMatches(request, parent.Id, parent.StartTime.ToUniversalTime().Ticks, parent.ProcessName,
                parent.MainModule?.FileName, executable, parent.HasExited)) return parent;
        }
        catch (Exception) { }
        parent?.Dispose();
        return null;
    }

    public static async Task<GpuActionResult> ResetAfterExitAsync(Func<Task> waitForExit, Func<Task<GpuActionResult>> reset)
    {
        await waitForExit().ConfigureAwait(false); // Failure must never issue a device command.
        try { return await reset().ConfigureAwait(false); }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    public static void WaitForIdle(TimeSpan timeout, string mutexName = MutexName)
    {
        Mutex mutex;
        try { mutex = Mutex.OpenExisting(mutexName); }
        catch (WaitHandleCannotBeOpenedException) { return; }
        using (mutex)
        {
            bool acquired;
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("独显设备仍在重启，控制中心尚未创建界面。请稍后重新打开。");
            mutex.ReleaseMutex();
        }
    }

    public static void ShowError(string message) => MessageBox(0, message, "Yoga控制中心：重启独显设备", 0x10);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint flags);
}
