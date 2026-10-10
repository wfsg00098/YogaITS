using System.Diagnostics;

namespace YogaITS.Tray.Services;

public sealed record StartupTaskResult(bool Success, string Message);

public static class StartupTaskHelper
{
    private const string TaskName = "Yoga ITS Tray";

    public static bool IsEnabled()
    {
        try
        {
            // IMPORTANT:
            // This method is called from the WinUI UI thread when the tray menu
            // opens. Do not call an async method and block on GetResult() here,
            // because its continuation may need the same DispatcherQueue and
            // deadlock the UI before the Mica window is created.
            return RunSchtasksExitCodeSync(
                "/Query", "/TN", TaskName) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int RunSchtasksExitCodeSync(params string[] arguments)
    {
        string exe = Path.Combine(
            Environment.SystemDirectory,
            "schtasks.exe");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using Process? process = Process.Start(psi);

        if (process is null)
            throw new InvalidOperationException(
                "无法启动 schtasks.exe。");

        process.WaitForExit();
        return process.ExitCode;
    }

    public static async Task<StartupTaskResult> SetEnabledAsync(bool enabled)
    {
        return enabled
            ? await EnableAsync()
            : await DisableAsync();
    }

    private static async Task<StartupTaskResult> EnableAsync()
    {
        string? exePath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return new(
                false,
                "无法确定当前 EXE 路径，未创建开机启动任务。");
        }

        // /IT: interactive-only, so the tray UI appears in this user's session.
        // /RL HIGHEST: Task Scheduler launches the app elevated at logon.
        // The app itself is already elevated while creating this task, so there is
        // no additional prompt here, and future logon starts do not show UAC.
        string taskCommand = $"\"{exePath}\" --startup";

        ProcessResult result = await RunSchtasksAsync(
            "/Create",
            "/TN", TaskName,
            "/TR", taskCommand,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/IT",
            "/F");

        if (result.ExitCode == 0)
        {
            return new(
                true,
                "已启用开机自启动。登录时由任务计划程序以最高权限启动，不会弹出 UAC。");
        }

        return new(
            false,
            $"创建计划任务失败（ExitCode={result.ExitCode}）。\n\n{BestMessage(result)}");
    }

    private static async Task<StartupTaskResult> DisableAsync()
    {
        // Treat an already-missing task as successfully disabled.
        if (!IsEnabled())
            return new(true, "开机自启动已关闭。");

        ProcessResult result = await RunSchtasksAsync(
            "/Delete",
            "/TN", TaskName,
            "/F");

        if (result.ExitCode == 0)
            return new(true, "开机自启动已关闭。");

        return new(
            false,
            $"删除计划任务失败（ExitCode={result.ExitCode}）。\n\n{BestMessage(result)}");
    }

    private static async Task<ProcessResult> RunSchtasksAsync(params string[] arguments)
    {
        string exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using Process? process = Process.Start(psi);

        if (process is null)
            throw new InvalidOperationException("无法启动 schtasks.exe。");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new(
            process.ExitCode,
            (await stdoutTask).Trim(),
            (await stderrTask).Trim());
    }

    private static string BestMessage(ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            return result.StdErr;

        if (!string.IsNullOrWhiteSpace(result.StdOut))
            return result.StdOut;

        return "schtasks.exe 没有返回详细信息。";
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StdOut,
        string StdErr);
}
