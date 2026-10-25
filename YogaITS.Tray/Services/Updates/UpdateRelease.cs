using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YogaITS.Tray.Services.Updates;

internal sealed record UpdateRelease(string Version, string Commit, Uri DownloadUrl, long Size, string Sha256,
    UiBuildFlavor Flavor = UiBuildFlavor.Native);

internal static class UpdatePolicy
{
    public const string Repository = "wfsg00098/YogaITS";
    public const long MaximumArchiveBytes = 150L * 1024 * 1024;
    public static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static string CurrentIdentity => typeof(UpdatePolicy).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "5.0-local";
    public static string CurrentVersion => CurrentIdentity.Split('+')[0];

    public static Uri ViaProxy(Uri original) => new("https://gh-proxy.org/" + original.AbsoluteUri);

    public static UpdateRelease ParseRelease(string json, UiBuildFlavor flavor = AppBuild.Flavor)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("不是正式发布版本。");
        string version = root.GetProperty("tag_name").GetString() ?? "";
        var match = Regex.Match(version, @"^\d+\.\d+-(?:(?<time>\d{14})-)?(?<hash>[0-9a-f]{8})$", RegexOptions.CultureInvariant);
        if (!match.Success || (match.Groups["time"].Success &&
            !DateTime.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)))
            throw new InvalidDataException("无法识别发布版本号。");
        string name = AppBuild.ArchiveName(version, flavor);
        var assets = root.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("发布中没有唯一的 Windows x64 更新包。");
        var asset = assets[0];
        string expectedUrl = $"https://github.com/{Repository}/releases/download/{version}/{name}";
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl)
            throw new InvalidDataException("更新包地址不属于预期的 GitHub 仓库。");
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
        if (size <= 0 || size > MaximumArchiveBytes || !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("更新包缺少有效的大小或 SHA-256 信息。");
        string target = root.GetProperty("target_commitish").GetString() ?? "";
        string commit = Regex.IsMatch(target, @"^[0-9a-fA-F]{40}$") ? target : match.Groups["hash"].Value;
        if (!commit.StartsWith(match.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发布版本与提交标识不一致。");
        return new(version, commit, new(expectedUrl), size, digest[7..], flavor);
    }

    public static bool IsNewer(UpdateRelease release, string currentIdentity)
    {
        string[] parts = currentIdentity.Split('+', 2);
        if (parts[0].Equals(release.Version, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length == 2 && parts[1].StartsWith(release.Commit, StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryVersion(release.Version, out var offered, out var offeredTime) ||
            !TryVersion(parts[0], out var current, out var currentTime) || offeredTime is null) return false;
        // Both dimensions must move forward: never offer a lower series, nor an
        // older/equal timestamp with another hash (including a delayed release).
        int series = offered.CompareTo(current);
        return series >= 0 && (currentTime is { } time ? offeredTime > time : series > 0);
    }

    private static bool TryVersion(string value, out Version series, out DateTime? timestamp)
    {
        series = new Version(0, 0);
        timestamp = null;
        var match = Regex.Match(value, @"^(?<series>\d+\.\d+)(?:-(?:(?<time>\d{14})-)?[0-9a-f]{8})?$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups["series"].Value, out var parsed)) return false;
        series = parsed;
        if (!match.Groups["time"].Success) return true;
        if (!DateTime.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) return false;
        timestamp = time;
        return true;
    }
}
