using System.Diagnostics;
using System.Management;
using YogaITS.Tray.Services;

namespace YogaITS.dGPURestart;

internal static class NvidiaDeviceControl
{
    public static async Task<GpuActionResult> RestartAsync()
    {
        string? instanceId = FindInstanceId();
        if (string.IsNullOrWhiteSpace(instanceId))
            return new(false, "没有找到 PCI\\VEN_10DE 的 NVIDIA 显示设备。");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/restart-device");
        start.ArgumentList.Add(instanceId);
        using var process = Process.Start(start);
        if (process is null) return new(false, "无法启动 pnputil.exe。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            try { process.Kill(); } catch { }
            return new(false, "重启设备等待超时，实际状态待确认。未重复发送重启命令。");
        }
        string output = (await stdout).Trim();
        string error = (await stderr).Trim();
        return process.ExitCode == 0 ? new(true, "NVIDIA 显示设备已重启。")
            : new(false, $"pnputil 返回 {process.ExitCode}。\n\n" + (string.IsNullOrEmpty(error) ? output : error));
    }

    private static string? FindInstanceId()
    {
        using var search = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass='Display'");
        using var devices = search.Get();
        foreach (ManagementObject device in devices)
        {
            using (device)
                if (device["PNPDeviceID"] is string id && id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase))
                    return id;
        }
        return null;
    }
}
