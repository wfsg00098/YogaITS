using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Updates;

internal static class BuildFlavorChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAILED flavor check: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        const string version = "6.0-20261025080000-abcdef12";
        string Metadata(params UiBuildFlavor[] flavors) => JsonSerializer.Serialize(new
        {
            tag_name = version, draft = false, prerelease = false, target_commitish = "abcdef12" + new string('0', 32),
            assets = flavors.Select(flavor => new
            {
                name = AppBuild.ArchiveName(version, flavor), size = 100,
                digest = "sha256:" + new string('0', 64),
                browser_download_url = $"https://github.com/{UpdatePolicy.Repository}/releases/download/{version}/{AppBuild.ArchiveName(version, flavor)}"
            })
        });
        string root = Path.Combine(Path.GetTempPath(), "YogaITS-Flavors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (UiBuildFlavor flavor in Enum.GetValues<UiBuildFlavor>())
            {
                var other = flavor == UiBuildFlavor.Native ? UiBuildFlavor.LiquidGlass : UiBuildFlavor.Native;
                var release = UpdatePolicy.ParseRelease(Metadata(other, flavor), flavor);
                Check(release.Flavor == flavor && release.DownloadUrl.AbsolutePath.EndsWith(AppBuild.ArchiveName(version, flavor)),
                    "Both assets in one release select the running flavor.");
                Reject(() => UpdatePolicy.ParseRelease(Metadata(other), flavor), "Missing flavor must not silently switch edition.");
                Reject(() => UpdatePolicy.ParseRelease(Metadata(flavor, flavor), flavor), "Duplicate flavor assets must fail.");
                using (var client = new GitHubUpdateClient(new MetadataHandler(Metadata(other, flavor)), flavor))
                    Check((await client.GetLatestAsync(default)).Flavor == flavor, "Both HTTP routes parse the configured flavor.");

                string folder = Path.Combine(root, flavor.ToString());
                string source = Path.Combine(folder, "source");
                Directory.CreateDirectory(source);
                foreach (string file in UpdatePackage.RequiredFilesFor(flavor))
                {
                    string path = Path.Combine(source, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "fixture " + file);
                }
                string marker = Path.Combine(source, "build-flavor.json");
                void SetMarker(UiBuildFlavor value) => File.WriteAllText(marker, JsonSerializer.Serialize(new
                {
                    flavor = value == UiBuildFlavor.Native ? "native" : "liquid-glass", executable = AppBuild.ExecutableFor(value)
                }));
                SetMarker(flavor);
                string lockFile = Path.Combine(source, "runtime.lock.json");
                string RuntimeLock(string sdk = "2.2.0") => JsonSerializer.Serialize(new
                {
                    windowsAppSdk = sdk,
                    files = Directory.GetFiles(source, "*.dll").ToDictionary(file => Path.GetFileName(file),
                        file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
                });
                if (flavor == UiBuildFlavor.LiquidGlass) File.WriteAllText(lockFile, RuntimeLock());
                int count = 0;
                string Extract()
                {
                    string archive = Path.Combine(folder, $"{++count}.zip");
                    ZipFile.CreateFromDirectory(source, archive);
                    string extracted = Path.Combine(folder, "extracted-" + count);
                    UpdatePackage.ExtractAndVerify(archive, extracted,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))), flavor);
                    return extracted;
                }
                string verified = Extract();
                Check(File.Exists(Path.Combine(verified, AppBuild.ExecutableFor(flavor))), "Complete package verified.");
                SetMarker(other);
                Reject(() => Extract(), "Cross-flavor marker rejected before installation.");
                SetMarker(flavor);
                if (flavor == UiBuildFlavor.LiquidGlass)
                {
                    File.WriteAllText(lockFile, RuntimeLock().Replace("Microsoft.UI.Xaml.dll", "Microsoft.ui.xaml.dll"));
                    Check(File.Exists(Path.Combine(Extract(), "Microsoft.UI.Xaml.dll")), "SDK filename casing matches Windows lookup semantics.");
                    File.WriteAllText(lockFile, RuntimeLock("2.5.1"));
                    Reject(() => Extract(), "Wrong bundled WinUI version rejected.");
                    File.WriteAllText(lockFile, RuntimeLock());
                    string dll = Path.Combine(source, "Microsoft.UI.Xaml.dll");
                    string saved = File.ReadAllText(dll);
                    File.WriteAllText(dll, "tampered runtime");
                    Reject(() => Extract(), "Bundled runtime hash mismatch rejected.");
                    File.WriteAllText(dll, saved);
                    File.Delete(lockFile);
                    Reject(() => Extract(), "Missing runtime lock rejected.");
                }
                else
                {
                    File.Delete(marker);
                    Check(File.Exists(Path.Combine(Extract(), "YogaITS.Tray.exe")), "Legacy native packages without markers remain supported.");
                }
                string target = Path.Combine(folder, "installed");
                Directory.CreateDirectory(target);
                string exe = Path.Combine(target, AppBuild.ExecutableFor(flavor));
                File.WriteAllText(exe, "old installation");
                File.WriteAllText(Path.Combine(target, "settings.json"), "preserve");
                bool restarted = false;
                UpdateInstallation.Apply(verified, target, () => restarted = true, flavor);
                Check(restarted && File.ReadAllText(exe).StartsWith("fixture"), "Installation targets the selected executable.");
                Check(File.ReadAllText(Path.Combine(target, "settings.json")) == "preserve", "Both editions preserve existing settings.");
                string rollback = Path.Combine(folder, "rollback/source");
                Directory.CreateDirectory(rollback);
                File.WriteAllText(Path.Combine(rollback, AppBuild.ExecutableFor(flavor)), "replacement");
                try { UpdateInstallation.Apply(rollback, target, () => throw new IOException("restart fixture"), flavor); }
                catch (IOException) { }
                Check(File.ReadAllText(exe).StartsWith("fixture"), "Both editions roll back a failed restart.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        return checks;
    }

    private sealed class MetadataHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
