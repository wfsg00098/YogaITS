using System.Diagnostics;
using System.Text.Json;

namespace YogaITS.Tray.Services.Updates;

internal sealed class AppUpdateService : IDisposable
{
    private readonly GitHubUpdateClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _monitor;
    public event Action? Changed;
    public UpdateRelease? Available { get; private set; }
    public bool Busy { get; private set; }

    public AppUpdateService() => _monitor = MonitorAsync();

    private async Task MonitorAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token).ConfigureAwait(false);
            try
            {
                await Task.Run(() => UpdateStaging.RemoveExpiredCompleted(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YogaITS", "Updates"), DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine("Update cleanup: " + ex.GetType().Name); }
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    var release = await _client.GetLatestAsync(_lifetime.Token);
                    Available = UpdatePolicy.IsNewer(release, UpdatePolicy.CurrentIdentity) ? release : null;
                    Changed?.Invoke();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine("Update check unavailable: " + ex.GetType().Name); }
                finally { _gate.Release(); }
            } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) { }
    }

    public async Task StartInstallerAsync(UpdateRelease release)
    {
        if (release.Flavor != AppBuild.Flavor) throw new InvalidDataException("更新包与当前构建类型不一致。");
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            Busy = true;
            Changed?.Invoke();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YogaITS", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string archive = Path.Combine(root, "release.zip");
            string extracted = Path.Combine(root, "files");
            string runner = Path.Combine(root, "runner");
            Directory.CreateDirectory(runner);
            // Run our current helper from staging, never from the directory being replaced.
            foreach (string name in new[] { "YogaITS.Updater.exe", "YogaITS.Updater.dll", "YogaITS.Updater.deps.json", "YogaITS.Updater.runtimeconfig.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(runner, name));
            await _client.DownloadAsync(release, archive, _lifetime.Token);
            await Task.Run(() => UpdatePackage.ExtractAndVerify(archive, extracted, release.Sha256), _lifetime.Token);
            using var parent = Process.GetCurrentProcess();
            var plan = new UpdatePlan(archive, release.Sha256, AppContext.BaseDirectory,
                parent.Id, parent.StartTime.ToUniversalTime().Ticks, AppBuild.Flavor);
            string planPath = Path.Combine(root, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), _lifetime.Token);
            var start = new ProcessStartInfo(Path.Combine(runner, "YogaITS.Updater.exe")) { UseShellExecute = false, WorkingDirectory = runner };
            start.ArgumentList.Add(planPath);
            using var installer = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
            try
            {
                string ready = Path.Combine(root, "ready");
                var wait = Stopwatch.StartNew();
                while (!File.Exists(ready))
                {
                    if (installer.HasExited || wait.Elapsed > TimeSpan.FromSeconds(10))
                        throw new IOException("更新助手未能就绪，现有程序将保持运行。");
                    await Task.Delay(100, _lifetime.Token);
                }
            }
            catch
            {
                try { if (!installer.HasExited) installer.Kill(); } catch { }
                throw;
            }
            // The helper waits for this process to exit before touching installed files.
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _ = CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        await _monitor;
        await _gate.WaitAsync();
        try { _client.Dispose(); _lifetime.Dispose(); }
        finally { _gate.Release(); }
    }
}
