using System.IO.Compression;
using System.Security.Cryptography;

namespace YogaITS.Tray.Services.Updates;

internal static class UpdatePackage
{
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

    public static void ExtractAndVerify(string archive, string directory, string expectedHash)
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
        foreach (string name in new[] { "YogaITS.Tray.exe", "YogaITS.Tray.dll", "YogaITS.Tray.pri", "YogaITS.Tray.runtimeconfig.json", "YogaITS.Tray.deps.json" })
            if (!File.Exists(Path.Combine(directory, name))) throw new InvalidDataException("更新包缺少文件：" + name);
    }
}

internal sealed record UpdatePlan(string Archive, string Sha256, string TargetDirectory,
    int ParentId, long ParentStartedUtcTicks);
