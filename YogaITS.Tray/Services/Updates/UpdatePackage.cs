using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace YogaITS.Tray.Services.Updates;

internal static class UpdatePackage
{
    internal static string[] RequiredFiles => RequiredFilesFor(AppBuild.Flavor);
    internal static string[] RequiredFilesFor(UiBuildFlavor flavor) =>
        MainFiles(flavor).Concat(HelperFiles).Concat(flavor == UiBuildFlavor.LiquidGlass ? GlassFiles : []).ToArray();
    private static string[] MainFiles(UiBuildFlavor flavor)
    {
        string name = Path.GetFileNameWithoutExtension(AppBuild.ExecutableFor(flavor));
        return [name + ".exe", name + ".dll", name + ".pri", name + ".runtimeconfig.json", name + ".deps.json"];
    }
    private static readonly string[] GlassFiles =
    [
        "build-flavor.json", "runtime.lock.json", "Start-YogaITS.ps1", "CaptureIdentity/AppxManifest.xml",
        "CaptureIdentity/StoreLogo.png", "CaptureIdentity/Square150x150Logo.png", "CaptureIdentity/Square44x44Logo.png",
        "LiquidGlassWinUI.dll", "CustomEffectRuntimeNative.dll", "Microsoft.UI.Xaml.dll", "Microsoft.WindowsAppRuntime.dll",
        "Microsoft.Graphics.Canvas.dll", "YogaITS.DesktopCapture_lg.dll", "CaptureIdentity/resources.pri", "resources.pri",
        .. new[] { 16,20,24,30,32,36,40,48,60,64,72,80,96,256 }.SelectMany(size =>
            new[] { "", "_altform-unplated", "_altform-lightunplated" }.Select(form =>
                $"CaptureIdentity/Square44x44Logo.targetsize-{size}{form}.png"))
    ];
    private static readonly string[] HelperFiles =
    [
        "YogaITS.Updater.exe", "YogaITS.Updater.dll", "YogaITS.Updater.runtimeconfig.json", "YogaITS.Updater.deps.json",
        "YogaITS.dGPUKeepAwake.exe", "YogaITS.dGPUKeepAwake.dll", "YogaITS.dGPUKeepAwake.runtimeconfig.json", "YogaITS.dGPUKeepAwake.deps.json",
        "YogaITS.dGPURestart.exe", "YogaITS.dGPURestart.dll", "YogaITS.dGPURestart.runtimeconfig.json", "YogaITS.dGPURestart.deps.json"
    ];
    public static void VerifyArchive(string archive, string expectedHash)
    {
        using var stream = File.OpenRead(archive);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包 SHA-256 校验失败，未修改现有程序。");
    }

    public static string SafeEntryPath(string directory, string entry)
    {
        if (string.IsNullOrEmpty(entry) || Path.IsPathRooted(entry) || entry.Contains(':') || entry.Contains('\\'))
            throw new InvalidDataException("更新包包含不安全的路径。");
        foreach (string segment in entry.TrimEnd('/').Split('/'))
        {
            string stem = segment.Split('.')[0];
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException("更新包包含不安全的文件名。");
        }
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, entry));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包路径超出临时目录。");
        return path;
    }

    public static void ExtractAndVerify(string archive, string directory, string expectedHash,
        UiBuildFlavor flavor = AppBuild.Flavor)
    {
        VerifyArchive(archive, expectedHash);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 1000 || zip.Entries.Sum(entry => entry.Length) > 400L * 1024 * 1024)
            throw new InvalidDataException("更新包内容大小异常。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string path = SafeEntryPath(directory, entry.FullName);
            if (!seen.Add(path)) throw new InvalidDataException("更新包包含重复路径。");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
        foreach (string name in RequiredFilesFor(flavor))
            if (!File.Exists(Path.Combine(directory, name))) throw new InvalidDataException("更新包缺少文件：" + name);
        string marker = Path.Combine(directory, "build-flavor.json");
        bool requireChecksums = false;
        if (File.Exists(marker))
        {
            using var metadata = ReadManifest(marker);
            string expected = flavor == UiBuildFlavor.LiquidGlass ? "liquid-glass" : "native";
            if (metadata.RootElement.GetProperty("flavor").GetString() != expected ||
                metadata.RootElement.GetProperty("executable").GetString() != AppBuild.ExecutableFor(flavor))
                throw new InvalidDataException("更新包构建类型不匹配。");
            if (metadata.RootElement.TryGetProperty("checksumManifest", out var checksumManifest))
            {
                if (checksumManifest.GetString() != "SHA256SUMS.txt")
                    throw new InvalidDataException("更新包文件校验清单无效。");
                requireChecksums = true;
            }
        }
        VerifyFileChecksums(directory, requireChecksums);
        if (flavor == UiBuildFlavor.LiquidGlass) VerifyBundledRuntime(directory);
    }

    internal static void VerifyFileChecksums(string directory, bool required)
    {
        directory = Path.GetFullPath(directory);
        string manifest = Path.Combine(directory, "SHA256SUMS.txt");
        // Older releases still require the trusted Release ZIP digest. New
        // build markers require complete per-file coverage as an extra check.
        if (!File.Exists(manifest))
        {
            if (required) throw new InvalidDataException("更新包缺少 SHA256SUMS.txt。");
            return;
        }
        if (new FileInfo(manifest).Length > 1024 * 1024)
            throw new InvalidDataException("更新包文件校验清单过大。");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(manifest))
        {
            if (listed.Count >= 1000 || line.Length < 67 || line[64] != ' ' || line[65] != ' ' ||
                !System.Text.RegularExpressions.Regex.IsMatch(line[..64], @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("更新包文件校验清单格式错误。");
            string path = SafeEntryPath(directory, line[66..]);
            if (path.Equals(manifest, StringComparison.OrdinalIgnoreCase) || !listed.Add(path))
                throw new InvalidDataException("更新包文件校验清单包含重复项或自身。");
            if (!File.Exists(path)) throw new InvalidDataException("更新包缺少校验文件：" + line[66..]);
            using var content = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(content)).Equals(line[..64], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包文件 SHA-256 校验失败：" + line[66..]);
        }
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(manifest, StringComparison.OrdinalIgnoreCase));
        if (listed.Count == 0 || !listed.SetEquals(files))
            throw new InvalidDataException("更新包文件未被 SHA-256 清单完整覆盖。");
    }

    private static JsonDocument ReadManifest(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("更新清单过大。");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static void VerifyBundledRuntime(string directory)
    {
        using var manifest = ReadManifest(Path.Combine(directory, "runtime.lock.json"));
        if (manifest.RootElement.GetProperty("windowsAppSdk").GetString() != "2.2.0")
            throw new InvalidDataException("玻璃构建运行时版本不匹配。");
        var files = manifest.RootElement.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Object || files.EnumerateObject().Count() is 0 or > 500)
            throw new InvalidDataException("玻璃构建运行时清单无效。");
        // SDK packs use mixed filename casing; Windows file lookup is insensitive.
        // Reject aliases while matching required DLL names with the same semantics.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateObject())
        {
            if (Path.GetFileName(file.Name) != file.Name || !file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !names.Add(file.Name))
                throw new InvalidDataException("运行时清单包含异常路径。");
            VerifyArchive(SafeEntryPath(directory, file.Name), file.Value.GetString() ?? "");
        }
        foreach (string required in GlassFiles.Where(file => file.EndsWith(".dll", StringComparison.Ordinal)))
            if (!names.Contains(required)) throw new InvalidDataException("运行时清单缺少：" + required);
    }
}

internal sealed record UpdatePlan(string Archive, string Sha256, string TargetDirectory,
    int ParentId, long ParentStartedUtcTicks, UiBuildFlavor Flavor = UiBuildFlavor.Native);
