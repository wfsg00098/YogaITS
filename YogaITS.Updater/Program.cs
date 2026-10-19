using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using YogaITS.Tray.Services.Updates;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        UpdatePlan? plan = null;
        bool parentExited = false;
        try
        {
            if (args.Length != 1) throw new InvalidOperationException("请从 YogaITS 的更新卡片启动更新。");
            plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(args[0]))
                ?? throw new InvalidDataException("更新计划无效。");
            UpdatePackage.VerifyArchive(plan.Archive, plan.Sha256);
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(args[0])!, "ready"), "ready");
            try
            {
                using var parent = Process.GetProcessById(plan.ParentId);
                if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartedUtcTicks)
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch (ArgumentException) { } // Parent already exited.
            parentExited = true;
            string verified = Path.Combine(Path.GetDirectoryName(plan.Archive)!, "verified");
            // Re-extract the authenticated archive instead of trusting the earlier staging copy.
            UpdatePackage.ExtractAndVerify(plan.Archive, verified, plan.Sha256);
            UpdateInstallation.Apply(verified, plan.TargetDirectory, () => Restart(plan.TargetDirectory));
            // Failed/partial rollback directories never receive this marker.
            try { await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(plan.Archive)!, "completed"), "YogaITS.Update.Completed.v1"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _ = MessageBox(0, "更新未完成：" + ex.Message + "\n\n现有配置和校色缓存不会被修改。", "Yoga控制中心更新", 0x10);
            if (parentExited && plan is not null && ex is not AggregateException)
            {
                try { Restart(plan.TargetDirectory); } catch { }
            }
        }
    }

    private static void Restart(string directory)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(directory, "YogaITS.Tray.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = directory
        }) ?? throw new IOException("无法重新启动 YogaITS。");
        if (process.WaitForExit(5000) && process.ExitCode != 0)
            throw new IOException("新版程序启动失败，将恢复旧版。");
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
