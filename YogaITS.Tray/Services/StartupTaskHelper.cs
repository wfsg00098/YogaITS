using System.Diagnostics;
using System.Security.Principal;

namespace YogaITS.Tray.Services;

public sealed record StartupTaskResult(bool Success, string Message, bool? Enabled = null);

public static class StartupTaskHelper
{
    private const string TaskName = "Yoga ITS Tray";
    private const int TaskNotFound = unchecked((int)0x80070002);
    private static readonly SemaphoreSlim MutationGate = new(1, 1);

    public static bool IsEnabled()
    {
        try
        {
            // IMPORTANT:
            // This method is called from the WinUI UI thread when the tray menu
            // opens. Do not call an async method and block on GetResult() here,
            // because its continuation may need the same DispatcherQueue and
            // deadlock the UI before the Mica window is created.
            ProcessResult result = QueryTaskSync();
            return result.ExitCode == 0 &&
                StartupTaskPolicy.IsEnabledForExecutable(result.StdOut, Environment.ProcessPath);
        }
        catch
        {
            return false;
        }
    }

    private static ProcessResult QueryTaskSync()
    {
        ProcessStartInfo psi = CreateStartInfo("/Query", "/TN", TaskName, "/XML", "/HRESULT");

        using Process? process = Process.Start(psi);

        if (process is null)
            throw new InvalidOperationException(
                "无法启动 schtasks.exe。");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            throw new TimeoutException("读取开机启动任务超时。");
        }
        return new(process.ExitCode, stdout.GetAwaiter().GetResult().Trim(), stderr.GetAwaiter().GetResult().Trim());
    }

    public static async Task<StartupTaskResult> SetEnabledAsync(bool enabled)
    {
        await MutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StartupTaskResult result;
            try
            {
                result = enabled
                    ? await EnableAsync().ConfigureAwait(false)
                    : await DisableAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new(false, $"开机自启操作失败：{ex.Message}");
            }
            bool actual = false;
            try
            {
                ProcessResult query = await QueryTaskAsync().ConfigureAwait(false);
                actual = query.ExitCode == 0 &&
                    StartupTaskPolicy.IsEnabledForExecutable(query.StdOut, Environment.ProcessPath);
            }
            catch { /* Unknown/unreadable does not count as enabled. */ }
            if (result.Success && actual != enabled)
                return new(false, "计划任务操作已执行，但回读未确认当前程序的开机自启状态。", actual);
            return result with { Enabled = actual };
        }
        finally { MutationGate.Release(); }
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

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? userSid = identity.User?.Value;
        if (userSid is null) return new(false, "无法确定当前用户，未创建开机启动任务。");

        string xml = StartupTaskPolicy.CreateDefinition(TaskName, exePath, userSid);
        string tempFile = Path.GetTempFileName();
        ProcessResult result;
        try
        {
            // Prepare the full definition before removing the old task.
            await File.WriteAllTextAsync(tempFile, xml).ConfigureAwait(false);
            StartupTaskResult deletion = await DisableAsync().ConfigureAwait(false);
            if (!deletion.Success) return deletion;

            // Explicit XML avoids schtasks defaults: AC-only and a 72-hour limit.
            // InteractiveToken/HighestAvailable preserve elevated UI logon startup.
            result = await RunSchtasksAsync("/Create", "/TN", TaskName, "/XML", tempFile).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }

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
        ProcessResult result = await RunSchtasksAsync(
            "/Delete",
            "/TN", TaskName,
            "/F").ConfigureAwait(false);

        if (result.ExitCode == 0)
            return new(true, "开机自启动已关闭。");

        // Missing is distinct from unreadable, disabled, or pointing to another EXE.
        ProcessResult query = await QueryTaskAsync().ConfigureAwait(false);
        if (query.ExitCode == TaskNotFound)
            return new(true, "开机自启动已关闭。");

        return new(
            false,
            $"删除计划任务失败（ExitCode={result.ExitCode}）。\n\n{BestMessage(result)}");
    }

    private static Task<ProcessResult> QueryTaskAsync() =>
        RunSchtasksAsync("/Query", "/TN", TaskName, "/XML", "/HRESULT");

    private static ProcessStartInfo CreateStartInfo(params string[] arguments)
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

        return psi;
    }

    private static async Task<ProcessResult> RunSchtasksAsync(params string[] arguments)
    {
        using Process? process = Process.Start(CreateStartInfo(arguments));

        if (process is null)
            throw new InvalidOperationException("无法启动 schtasks.exe。");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException("计划任务操作超时，请刷新状态后重试。");
        }

        return new(
            process.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).Trim(),
            (await stderrTask.ConfigureAwait(false)).Trim());
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
