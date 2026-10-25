namespace YogaITS.Tray.Services.Color;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

internal sealed record CxfIdentity(bool Ok, string ComputerName, string ComputerSerialNumber, string DisplayHardwareId, string Detail);
internal sealed record FactoryValidationResult(
    bool Ok,
    string Message,
    IReadOnlyList<GamutProfile> Profiles,
    int ProfileCount,
    bool NativePresent,
    bool Mhc2Present,
    IReadOnlyList<string>? CorruptProfiles = null,
    string Fingerprint = "");

internal static class IccInspector
{
    public static bool HasTag(string path, string signature)
    {
        try { return TryGetTag(File.ReadAllBytes(path), signature, out _); }
        catch { return false; }
    }

    public static CxfIdentity ReadCxfIdentity(string path)
    {
        try
        {
            byte[] b = File.ReadAllBytes(path);
            if (!TryGetTag(b, "CxF ", out var tag))
                return new(false, "", "", "", "ICC has no CxF tag.");
            if (tag.Length < 9 || Encoding.ASCII.GetString(tag, 0, 4) != "ZXML")
                return new(false, "", "", "", "CxF tag is not ZXML.");

            using var src = new MemoryStream(tag, 8, tag.Length - 8, writable: false);
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            z.CopyTo(dst);
            string xml = Encoding.UTF8.GetString(dst.ToArray()).TrimEnd('\0');
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var e = doc.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("CustomAttributes", StringComparison.OrdinalIgnoreCase));
            if (e is null) return new(false, "", "", "", "CxF XML has no CustomAttributes element.");

            string Attr(string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "";
            string computer = Attr("ComputerName");
            string serial = Attr("ComputerSerialNumber");
            string panel = Attr("DisplayHardwareId");
            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(panel))
                return new(false, computer, serial, panel, "CxF identity is incomplete.");
            return new(true, computer, serial, panel, "CxF identity parsed.");
        }
        catch (Exception ex)
        {
            return new(false, "", "", "", ex.Message);
        }
    }

    public static FactoryValidationResult ValidateFactoryDirectory(string root, MachineIdentity? machine, string? panelHardwareId)
    {
        if (!Directory.Exists(root))
            return new(false, "Factory cache directory does not exist.", [], 0, false, false);
        var valid = new List<GamutProfile>();
        var corrupt = new List<string>();
        var fingerprints = new List<string>();
        bool readable = true;
        foreach (string path in ProfileService.EnumerateProfiles(root))
        {
            if (IccCalibrationData.Validate(path, out _))
                valid.Add(new(ProfileService.ModeFromFileName(path), path));
            else corrupt.Add(path);
            try { fingerprints.Add(Path.GetRelativePath(root, path) + ":" + Sha256(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { readable = false; }
        }
        var profiles = valid.GroupBy(profile => profile.Mode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(profile => ProfileService.ModeOrder(profile.Mode))
            .ThenBy(profile => profile.Mode, StringComparer.OrdinalIgnoreCase).ToArray();
        bool native = profiles.Any(profile => profile.Mode.Equals("Native", StringComparison.OrdinalIgnoreCase));
        bool mhc2 = profiles.Any(profile => HasTag(profile.Path, "MHC2"));
        bool ok = profiles.Length > 0 && corrupt.Count == 0;
        string message = corrupt.Count > 0 ? $"{corrupt.Count} calibration file(s) are corrupt; redownload required."
            : profiles.Length == 0 ? "No ICC/ICM calibration files found."
            : $"VALID: {profiles.Length} Factory ICC/ICM profiles; calibration data validated.";
        string fingerprint = readable ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fingerprints)))) : "";
        return new(ok, message, profiles, profiles.Length, native, mhc2, corrupt, fingerprint);
    }

    public static bool ValidateIdentity(CxfIdentity identity, MachineIdentity machine, string? panelHardwareId, out string detail)
    {
        detail = identity.Detail;
        if (!identity.Ok) return false;
        if (string.IsNullOrWhiteSpace(panelHardwareId) ||
            !string.Equals(identity.DisplayHardwareId, panelHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            detail = "CxF panel identity does not match the current internal panel.";
            return false;
        }

        // Lenovo's online LEN8BAD package uses this exact pair of placeholders,
        // including in the factory files shipped with the machine. Do not treat
        // empty fields, other placeholders, or real identity mismatches as generic.
        bool panelOnly = string.Equals(identity.ComputerName, "INVALID", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(identity.ComputerSerialNumber, "INVALID", StringComparison.OrdinalIgnoreCase);
        if (!panelOnly)
        {
            if (!string.Equals(identity.ComputerSerialNumber, machine.SerialNumber, StringComparison.OrdinalIgnoreCase))
            {
                detail = "CxF computer serial does not match this machine.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(identity.ComputerName) &&
                !string.Equals(identity.ComputerName, machine.MachineType, StringComparison.OrdinalIgnoreCase))
            {
                detail = "CxF machine type does not match this machine.";
                return false;
            }
        }

        detail = panelOnly ? "CxF panel identity matched; Lenovo machine identity placeholders accepted." : "CxF machine/panel identity matched.";
        return true;
    }

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static bool TryGetTag(byte[] b, string signature, out byte[] tag)
    {
        tag = Array.Empty<byte>();
        if (b.Length < 132 || signature.Length != 4) return false;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(128, 4));
        for (uint i = 0; i < count; i++)
        {
            int e = checked(132 + (int)i * 12);
            if (e + 12 > b.Length) break;
            if (Encoding.ASCII.GetString(b, e, 4) != signature) continue;
            int off = checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e + 4, 4)));
            int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e + 8, 4)));
            if (off < 0 || size <= 0 || off > b.Length || size > b.Length - off) return false;
            tag = b.AsSpan(off, size).ToArray();
            return true;
        }
        return false;
    }
}
