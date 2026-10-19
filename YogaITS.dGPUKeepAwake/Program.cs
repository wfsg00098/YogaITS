using System.Diagnostics;
using YogaITS.Tray.Services;

namespace YogaITS.dGPUKeepAwake;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Validate ownership before touching DXGI, even when launched manually.
        if (args.Length != 4 || !int.TryParse(args[0], out int pid) || !long.TryParse(args[1], out long ticks)) return 2;
        using Process? parent = GetParent(pid, ticks);
        if (parent is null) return 2;
        try
        {
            using var stop = EventWaitHandle.OpenExisting(args[2]);
            using var check = EventWaitHandle.OpenExisting(args[3]);
            using var device = new NvidiaIdleContext();
            // Keep orphan cleanup independent of a potentially stuck driver call.
            // This is the helper's only autonomous periodic activity.
            using var lifetime = new CancellationTokenSource();
            _ = CheckParentAsync(parent, lifetime.Token);
            try
            {
                device.Create();
                Report(true);
                WaitHandle[] commands = [stop, check];
                while (WaitHandle.WaitAny(commands) == 1)
                    Report(device.IsUsable); // No retry/recreation or power policy here.
                return 0;
            }
            finally { lifetime.Cancel(); }
        }
        catch { return 1; }
    }

    private static void Report(bool active)
    {
        Console.WriteLine(active ? "ACTIVE" : "INVALID");
        Console.Out.Flush();
    }

    private static Process? GetParent(int pid, long ticks)
    {
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(pid);
            if (GpuKeepAwakePolicy.ParentMatches(pid, ticks, parent.Id,
                parent.StartTime.ToUniversalTime().Ticks, parent.ProcessName, parent.HasExited)) return parent;
        }
        catch { }
        parent?.Dispose();
        return null;
    }

    private static async Task CheckParentAsync(Process parent, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(GpuKeepAwakePolicy.CheckInterval);
            while (await timer.WaitForNextTickAsync(token))
                if (parent.HasExited) Environment.Exit(0);
        }
        catch (OperationCanceledException) { }
        catch { Environment.Exit(0); }
    }
}
