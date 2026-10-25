using System.Diagnostics;
using System.Security.Principal;
using YogaITS.Tray.Services;

namespace YogaITS.dGPURestart;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var request = GpuDeviceRestartProtocol.Parse(args);
        if (request is null) return 2;
        string executable = Path.Combine(AppContext.BaseDirectory, AppBuild.ExecutableName);
        using var parent = GpuDeviceRestartProtocol.OpenParent(request, executable);
        if (parent is null) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        try
        {
            GpuActionResult result;
            using (var gate = new Mutex(false, GpuDeviceRestartProtocol.MutexName))
            {
                bool acquired;
                try { acquired = gate.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 3;
                try
                {
                    using var ready = EventWaitHandle.OpenExisting(request.ReadyEvent);
                    ready.Set(); // Only now may Tray begin its graceful shutdown.
                    result = GpuDeviceRestartProtocol.ResetAfterExitAsync(
                        () => parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)),
                        NvidiaDeviceControl.RestartAsync).GetAwaiter().GetResult();
                }
                finally { gate.ReleaseMutex(); } // Main owns the mutex across the synchronous wait.
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (string argument in AppLaunchPolicy.RecoveryArguments(request.ControlCenter, request.MiniWindow))
                start.ArgumentList.Add(argument);
            using var tray = Process.Start(start) ?? throw new IOException("无法重新启动控制中心，请手动打开程序。");
            if (tray.WaitForExit(5000) && tray.ExitCode != 0)
                throw new IOException("控制中心重新启动失败，请手动打开程序。");
            if (!result.Success) GpuDeviceRestartProtocol.ShowError(result.Message + "\n\n控制中心已重新启动，未重复发送设备重启命令。");
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            GpuDeviceRestartProtocol.ShowError(ex.Message);
            return 1;
        }
    }
}
