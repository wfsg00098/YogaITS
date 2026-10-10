namespace YogaITS.Tray.Services.Color;

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

internal sealed record FactoryRecoveryResult(bool Ok, string Message, string FactoryRoot, int ProfileCount, long ZipBytes);

internal static class FactoryRecoveryService
{
    public static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YogaITS", "ColorCalibration");
    public static readonly string FactoryRoot = Path.Combine(CacheRoot, "Factory");
    public static readonly string ZipPath = Path.Combine(CacheRoot, "ColorCalibration.zip");

    private const string Endpoint = "https://supportapi.lenovo.com/v3/ColorCalibration/GetFileBySn?sn=";
    private const string ClientId = "Hr0Z3kZYsCync4KzAjLqqg==";

    public static FactoryValidationResult ValidateCurrent(MachineIdentity machine, DisplayInfo d) =>
        IccInspector.ValidateFactoryDirectory(FactoryRoot, machine, d.HardwareId);

    public static async Task<FactoryRecoveryResult> RecoverAsync(MachineIdentity machine, DisplayInfo d, Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(d.HardwareId))
            return new(false, "Current panel Hardware ID is unavailable.", FactoryRoot, 0, 0);

        Directory.CreateDirectory(CacheRoot);
        string staging = Path.Combine(CacheRoot, ".download_" + Guid.NewGuid().ToString("N"));
        string extract = Path.Combine(staging, "extract");
        string zipTmp = Path.Combine(staging, "ColorCalibration.zip");
        Directory.CreateDirectory(staging);

        try
        {
            string requestId = "1S" + machine.MachineType + machine.SerialNumber;
            progress?.Invoke($"Requesting Lenovo Factory Calibration package for MT={machine.MachineType}, serial=<masked> ...");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("ClientID", ClientId);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PC_WIN11");
            // The metadata can be JSON or a URL string; the same client also
            // downloads ZIP data. A JSON-only Accept can cause HTTP 406.
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            using var meta = await http.GetAsync(Endpoint + Uri.EscapeDataString(requestId));
            string body = await meta.Content.ReadAsStringAsync();
            if (!meta.IsSuccessStatusCode)
                return new(false, $"Metadata request failed: HTTP {(int)meta.StatusCode} {meta.ReasonPhrase}.", FactoryRoot, 0, 0);

            string? url = ExtractDownloadUrl(body);
            if (string.IsNullOrWhiteSpace(url))
                return new(false, "Metadata response did not contain a usable download URL.", FactoryRoot, 0, 0);

            progress?.Invoke("Metadata PASS. Downloading calibration ZIP ...");
            using var dl = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!dl.IsSuccessStatusCode)
                return new(false, $"ZIP download failed: HTTP {(int)dl.StatusCode} {dl.ReasonPhrase}.", FactoryRoot, 0, 0);
            byte[] zipBytes = await dl.Content.ReadAsByteArrayAsync();
            if (zipBytes.Length < 4 || zipBytes[0] != (byte)'P' || zipBytes[1] != (byte)'K')
                return new(false, "Downloaded payload is not a ZIP archive.", FactoryRoot, 0, zipBytes.LongLength);
            await File.WriteAllBytesAsync(zipTmp, zipBytes);

            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(zipTmp, extract, overwriteFiles: true);
            string? candidate = FindBestFactoryDirectory(extract);
            if (candidate is null)
                return new(false, "ZIP extracted, but no directory contained at least two recognized gamut ICC profiles.", FactoryRoot, 0, zipBytes.LongLength);

            var valid = IccInspector.ValidateFactoryDirectory(candidate, machine, d.HardwareId);
            if (!valid.Ok)
                return new(false, "Downloaded Factory package failed validation: " + valid.Message, FactoryRoot, valid.ProfileCount, zipBytes.LongLength);

            progress?.Invoke($"Validation PASS ({valid.ProfileCount} profiles). Replacing YogaITS Factory cache atomically ...");
            string newRoot = Path.Combine(CacheRoot, ".Factory.new." + Guid.NewGuid().ToString("N"));
            CopyDirectory(candidate, newRoot);
            // Validate the exact copy that will become live.
            var newValid = IccInspector.ValidateFactoryDirectory(newRoot, machine, d.HardwareId);
            if (!newValid.Ok)
            {
                Directory.Delete(newRoot, true);
                return new(false, "Staged cache copy failed re-validation: " + newValid.Message, FactoryRoot, newValid.ProfileCount, zipBytes.LongLength);
            }

            string? backup = null;
            try
            {
                if (Directory.Exists(FactoryRoot))
                {
                    backup = Path.Combine(CacheRoot, ".Factory.backup." + Guid.NewGuid().ToString("N"));
                    Directory.Move(FactoryRoot, backup);
                }
                Directory.Move(newRoot, FactoryRoot);
                File.Copy(zipTmp, ZipPath, true);
                if (backup is not null && Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch
            {
                try
                {
                    if (!Directory.Exists(FactoryRoot) && backup is not null && Directory.Exists(backup))
                        Directory.Move(backup, FactoryRoot);
                }
                catch { }
                throw;
            }

            return new(true,
                $"Online recovery PASS: {valid.ProfileCount} per-unit Factory profiles validated and cached. No serial value was written to the log.",
                FactoryRoot, valid.ProfileCount, zipBytes.LongLength);
        }
        catch (Exception ex)
        {
            return new(false, "Online recovery failed: " + ex.Message, FactoryRoot, 0, 0);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static string? ExtractDownloadUrl(string body)
    {
        string t = body.Trim();
        if (Uri.TryCreate(t.Trim('"'), UriKind.Absolute, out var plain) && plain.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return plain.ToString();
        try
        {
            using var doc = JsonDocument.Parse(t);
            return FindUrl(doc.RootElement);
        }
        catch { return null; }
    }

    private static string? FindUrl(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                string? s = e.GetString();
                if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u.ToString();
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    string? x = FindUrl(p.Value);
                    if (x is not null) return x;
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                {
                    string? y = FindUrl(x);
                    if (y is not null) return y;
                }
                break;
        }
        return null;
    }

    private static string? FindBestFactoryDirectory(string root)
    {
        var groups = Directory.EnumerateFiles(root, "*.icm", SearchOption.AllDirectories)
            .Select(p => new { Path = p, Mode = ProfileService.ModeFromFileName(p), Dir = Path.GetDirectoryName(p)! })
            .Where(x => x.Mode != "Unknown")
            .GroupBy(x => x.Dir, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Select(x => x.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ToList();
        return groups.FirstOrDefault(g => g.Select(x => x.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)?.Key;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
