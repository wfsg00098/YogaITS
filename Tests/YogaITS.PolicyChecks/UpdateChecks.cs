using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YogaITS.Tray.Services.Updates;

internal static class UpdateChecks
{
    public static async Task<int> RunAsync(string? updaterExecutable = null)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAILED update check: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { rejected = true; }
            Check(rejected, message);
        }
        const string tag = "5.0-20261018053120-abcdef12";
        const string commit = "abcdef1234567890abcdef1234567890abcdef12";
        byte[] bytes = Encoding.UTF8.GetBytes("test release payload");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string Json(string version = tag, string? digest = null, string? url = null, bool draft = false, bool prerelease = false) =>
            JsonSerializer.Serialize(new
            {
                tag_name = version, draft, prerelease, target_commitish = commit,
                assets = new[] { new { name = $"YogaITS-{version}-win-x64.zip", size = bytes.Length,
                    digest = digest ?? "sha256:" + hash,
                    browser_download_url = url ?? $"https://github.com/{UpdatePolicy.Repository}/releases/download/{version}/YogaITS-{version}-win-x64.zip" } }
            });
        var release = UpdatePolicy.ParseRelease(Json());
        Check(release.Version == tag && release.Commit == commit && release.Sha256 == hash, "release identity and digest parsed");
        Check(!UpdatePolicy.IsDifferent(release, tag + "+" + commit), "same release is hidden");
        Check(!UpdatePolicy.IsDifferent(release, "5.0+" + commit), "same source in local build is hidden");
        Check(UpdatePolicy.IsDifferent(release, "4.0+" + new string('f', 40)), "different GitHub latest source is offered without sorting hashes");
        Check(UpdatePolicy.ParseRelease(Json("4.0-abcdef12")).Version == "4.0-abcdef12", "old release names remain readable");
        Reject(() => UpdatePolicy.ParseRelease(Json(draft: true)), "draft rejected");
        Reject(() => UpdatePolicy.ParseRelease(Json(prerelease: true)), "prerelease rejected");
        Reject(() => UpdatePolicy.ParseRelease(Json(digest: "sha256:bad")), "missing or malformed digest rejected");
        Reject(() => UpdatePolicy.ParseRelease(Json(url: "https://example.com/update.zip")), "foreign URL rejected");
        Reject(() => UpdatePolicy.ParseRelease(Json("5.0-20261399053120-abcdef12")), "invalid UTC date rejected");
        Check(UpdatePolicy.ViaProxy(release.DownloadUrl).AbsoluteUri == "https://gh-proxy.org/" + release.DownloadUrl, "proxy keeps full original URL");

        using (var handler = new FakeHttp((_, _) => Task.FromResult(Response(Json()))))
        using (var client = new GitHubUpdateClient(handler))
        {
            Check((await client.GetLatestAsync(default)).Version == tag, "direct metadata succeeds");
            Check(handler.Requests.Count == 1 && handler.Requests[0].Host == "api.github.com", "no proxy when direct works");
        }
        foreach (bool invalidJson in new[] { false, true })
        {
            using var handler = new FakeHttp((index, _) => Task.FromResult(index == 1
                ? invalidJson ? Response("<html>gateway</html>") : new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Response(Json())));
            using var client = new GitHubUpdateClient(handler);
            Check((await client.GetLatestAsync(default)).Version == tag, "metadata errors fall back");
            Check(handler.Requests.Count == 2 && handler.Requests[1].Host == "gh-proxy.org", "metadata fallback uses requested proxy");
        }
        using (var handler = new FakeHttp((index, _) => index == 1
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException()) : Task.FromResult(Response(Json()))))
        using (var client = new GitHubUpdateClient(handler))
            Check((await client.GetLatestAsync(default)).Version == tag, "direct timeout falls back");
        using (var stop = new CancellationTokenSource())
        using (var handler = new FakeHttp((_, _) => { stop.Cancel(); return Task.FromCanceled<HttpResponseMessage>(stop.Token); }))
        using (var client = new GitHubUpdateClient(handler))
        {
            try { await client.GetLatestAsync(stop.Token); } catch (OperationCanceledException) { }
            Check(handler.Requests.Count == 1, "user cancellation does not retry proxy");
        }

        string temporary = Path.Combine(Path.GetTempPath(), "YogaITS-UpdateChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string download = Path.Combine(temporary, "download.zip");
            using (var handler = new FakeHttp((index, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   { Content = new ByteArrayContent(index == 1 ? new byte[bytes.Length] : bytes) })))
            using (var client = new GitHubUpdateClient(handler))
            {
                await client.DownloadAsync(release, download, default);
                Check(File.ReadAllBytes(download).SequenceEqual(bytes), "corrupt direct data retries and replaces file from proxy");
                Check(handler.Requests.Count == 2 && handler.Requests[1].Host == "gh-proxy.org", "download fallback reaches proxy");
            }
            Reject(() => UpdatePackage.VerifyArchive(download, new string('0', 64)), "bad archive digest rejected");
            foreach (string bad in new[] { "../outside", "/absolute", "C:/Windows/test", "sub/../../escape", "a\\..\\escape", "NUL.txt", "CON", "dir./file", "space /file" })
                Reject(() => UpdatePackage.SafeEntryPath(temporary, bad), "ZIP traversal rejected: " + bad);
            string archive = Path.Combine(temporary, "valid.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (string name in new[] { "YogaITS.Tray.exe", "YogaITS.Tray.dll", "YogaITS.Tray.pri", "YogaITS.Tray.runtimeconfig.json", "YogaITS.Tray.deps.json" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write("fixture " + name);
                }
            string archiveHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
            string extracted = Path.Combine(temporary, "extracted");
            UpdatePackage.ExtractAndVerify(archive, extracted, archiveHash);
            Check(File.Exists(Path.Combine(extracted, "YogaITS.Tray.pri")), "complete package extracted");
            string malicious = Path.Combine(temporary, "unsafe.zip");
            using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create)) zip.CreateEntry("../escape.exe");
            Reject(() => UpdatePackage.ExtractAndVerify(malicious, Path.Combine(temporary, "unsafe"),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(malicious)))) , "unsafe archive rejected before writing outside stage");
            Check(!File.Exists(Path.Combine(temporary, "escape.exe")), "no traversal file written");

            string target = Path.Combine(temporary, "installed");
            Directory.CreateDirectory(target);
            string exe = Path.Combine(target, "YogaITS.Tray.exe");
            File.WriteAllText(exe, "old program");
            File.WriteAllText(Path.Combine(target, "settings.json"), "preserve me");
            bool restarted = false;
            UpdateInstallation.Apply(extracted, target, () => restarted = true);
            Check(restarted && File.ReadAllText(exe) == "fixture YogaITS.Tray.exe", "successful install invokes restart after copying");
            Check(File.ReadAllText(Path.Combine(target, "settings.json")) == "preserve me", "unrelated configuration preserved");

            string rollbackSource = Path.Combine(temporary, "rollback", "source");
            Directory.CreateDirectory(rollbackSource);
            File.WriteAllText(Path.Combine(rollbackSource, "YogaITS.Tray.exe"), "replacement");
            File.WriteAllText(Path.Combine(rollbackSource, "new.dll"), "new file");
            Reject(() => UpdateInstallation.Apply(rollbackSource, target, () => throw new IOException("simulated restart failure")), "restart failure triggers rollback");
            Check(File.ReadAllText(exe) == "fixture YogaITS.Tray.exe" && !File.Exists(Path.Combine(target, "new.dll")), "rollback restores old file and removes newly copied file");

            string lockedSource = Path.Combine(temporary, "locked", "source");
            Directory.CreateDirectory(lockedSource);
            File.WriteAllText(Path.Combine(lockedSource, "YogaITS.Tray.exe"), "replacement");
            using (var locked = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => UpdateInstallation.Apply(lockedSource, target, () => throw new Exception("must not restart")), "locked installed executable safely aborts");
            Check(File.ReadAllText(exe) == "fixture YogaITS.Tray.exe", "locked old file remains intact");

            if (updaterExecutable is not null)
            {
                using var parent = Process.GetCurrentProcess();
                string planPath = Path.Combine(temporary, "plan.json");
                File.WriteAllText(planPath, JsonSerializer.Serialize(new UpdatePlan(archive, archiveHash, target,
                    parent.Id, parent.StartTime.ToUniversalTime().Ticks)));
                var start = new ProcessStartInfo(Path.GetFullPath(updaterExecutable)) { UseShellExecute = false };
                start.ArgumentList.Add(planPath);
                using var helper = Process.Start(start)!;
                try
                {
                    string ready = Path.Combine(temporary, "ready");
                    var timeout = Stopwatch.StartNew();
                    while (!File.Exists(ready) && !helper.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(10))
                        await Task.Delay(100);
                    Check(File.Exists(ready) && !helper.HasExited, "real updater executable confirms readiness");
                    Check(!Directory.Exists(Path.Combine(temporary, "verified")) &&
                        File.ReadAllText(exe) == "fixture YogaITS.Tray.exe", "helper waits for exact parent instead of modifying a live installation");
                }
                finally
                {
                    if (!helper.HasExited) helper.Kill();
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally { Directory.Delete(temporary, true); }
        return checks;
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class FakeHttp(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return respond(Requests.Count, cancellationToken);
        }
    }
}
