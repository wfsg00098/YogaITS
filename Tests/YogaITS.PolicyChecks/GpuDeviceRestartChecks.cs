using System.Diagnostics;
using YogaITS.Tray.Services;

internal static class GpuDeviceRestartChecks
{
    public static async Task<int> RunAsync(string? helperExecutable)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAILED GPU device restart check: " + message);
            checks++;
        }
        string ready = GpuDeviceRestartProtocol.ReadyPrefix + Guid.NewGuid().ToString("N");
        var request = new GpuDeviceRestartRequest(123, 456, ready, true, false);
        Check(GpuDeviceRestartProtocol.Parse(request.Arguments) == request, "private handoff round-trips parent identity and windows");
        foreach (string[] invalid in new[]
        {
            Array.Empty<string>(), new[] { "123", "456" }, new[] { "0", "456", ready, "1", "0" },
            new[] { "123", "0", ready, "1", "0" }, new[] { "-1", "456", ready, "1", "0" },
            new[] { "123", "456", "Global\\unrelated", "1", "0" }, new[] { "123", "456", ready, "true", "0" },
            new[] { "123", "456", ready + "x", "1", "0" }, new[] { "123", "456", ready, "1", "0", "extra" }
        }) Check(GpuDeviceRestartProtocol.Parse(invalid) is null, "malformed or unowned requests cannot reset hardware");
        const string exe = @"C:\Data\YogaITS\YogaITS.Tray.exe";
        Check(GpuDeviceRestartProtocol.ParentMatches(request, 123, 456, "YogaITS.Tray", exe, exe, false), "exact parent identity accepted");
        foreach (var (pid, ticks, name, path, exited) in new[]
        {
            (124, 456L, "YogaITS.Tray", exe, false), (123, 457L, "YogaITS.Tray", exe, false),
            (123, 456L, "other", exe, false), (123, 456L, "YogaITS.Tray", @"D:\old\YogaITS.Tray.exe", false),
            (123, 456L, "YogaITS.Tray", exe, true)
        }) Check(!GpuDeviceRestartProtocol.ParentMatches(request, pid, ticks, name, path, exe, exited),
            "PID reuse, another installation or an exited parent is rejected before readiness");
        foreach (bool main in new[] { false, true })
        foreach (bool mini in new[] { false, true })
        {
            string[] args = AppLaunchPolicy.RecoveryArguments(main, mini);
            Check(AppLaunchPolicy.ShouldOpenControlCenter(args) == main && AppLaunchPolicy.ShouldOpenMiniWindow(args) == mini,
                "recovery restores exactly the prior windows; mini-only recovery does not open the full window");
        }
        var parentExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int resets = 0;
        var reset = GpuDeviceRestartProtocol.ResetAfterExitAsync(() => parentExited.Task,
            () => { resets++; return Task.FromResult(new GpuActionResult(true, "fixture")); });
        Check(resets == 0 && !reset.IsCompleted, "no device command can run while the parent is alive");
        parentExited.SetResult();
        Check((await reset).Success && resets == 1, "one parent exit permits exactly one reset attempt");
        bool timeout = false;
        try
        {
            await GpuDeviceRestartProtocol.ResetAfterExitAsync(() => Task.FromException(new TimeoutException()),
                () => { resets++; return Task.FromResult(new GpuActionResult(true, "unexpected")); });
        }
        catch (TimeoutException) { timeout = true; }
        Check(timeout && resets == 1, "parent timeout never resets a device or kills the parent");
        var failed = await GpuDeviceRestartProtocol.ResetAfterExitAsync(() => Task.CompletedTask,
            () => throw new IOException("fixture command failure"));
        Check(!failed.Success && failed.Message.Contains("fixture"), "device failure is returned so the helper can still relaunch the tray");

        string mutexName = @"Local\YogaITS.Tests.GpuRestart." + Guid.NewGuid().ToString("N");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(true, mutexName);
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        owner.Start();
        try
        {
            Check(held.Wait(TimeSpan.FromSeconds(2)), "fixture owns the reset gate");
            bool blocked = false;
            try { GpuDeviceRestartProtocol.WaitForIdle(TimeSpan.Zero, mutexName); }
            catch (TimeoutException) { blocked = true; }
            Check(blocked, "new GUI startup must wait while reset owns the gate");
        }
        finally { release.Set(); owner.Join(); }
        GpuDeviceRestartProtocol.WaitForIdle(TimeSpan.Zero, mutexName);
        Check(true, "GUI startup proceeds after the reset gate is released");

        if (helperExecutable is not null)
        {
            using var current = Process.GetCurrentProcess();
            foreach (string[] args in new[] { Array.Empty<string>(),
                new GpuDeviceRestartRequest(current.Id, current.StartTime.ToUniversalTime().Ticks, ready, false, false).Arguments })
            {
                var start = new ProcessStartInfo(Path.GetFullPath(helperExecutable)) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in args) start.ArgumentList.Add(argument);
                using var helper = Process.Start(start)!;
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(helper.ExitCode == 2, "published helper rejects missing arguments and a non-Tray parent without hardware access");
            }
        }
        return checks;
    }
}
