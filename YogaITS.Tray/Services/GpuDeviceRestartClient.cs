using System.Diagnostics;

namespace YogaITS.Tray.Services;

internal static class GpuDeviceRestartClient
{
    public static async Task StartAsync(bool controlCenter, bool miniWindow)
    {
        using var parent = Process.GetCurrentProcess();
        var request = new GpuDeviceRestartRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            GpuDeviceRestartProtocol.ReadyPrefix + Guid.NewGuid().ToString("N"), controlCenter, miniWindow);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, request.ReadyEvent);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, GpuDeviceRestartProtocol.HelperName))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (string argument in request.Arguments) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动独显重启助手，未重启设备。");
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready.WaitOne(0))
            {
                if (helper.HasExited || elapsed.Elapsed > TimeSpan.FromSeconds(10))
                    throw new IOException("独显重启助手未能就绪，控制中心保持运行，未重启设备。");
                await Task.Delay(50);
            }
        }
        catch
        {
            try { if (!helper.HasExited) helper.Kill(); } catch { }
            throw;
        }
    }
}
