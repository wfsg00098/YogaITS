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
    bool Mhc2Present);

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

    public static FactoryValidationResult ValidateFactoryDirectory(string root, MachineIdentity machine, string? panelHardwareId)
    {
        if (string.IsNullOrWhiteSpace(panelHardwareId))
            return new(false, "Panel Hardware ID is unavailable.", Array.Empty<GamutProfile>(), 0, false, false);
        if (!Directory.Exists(root))
            return new(false, "Factory cache directory does not exist.", Array.Empty<GamutProfile>(), 0, false, false);

        var profiles = Directory.EnumerateFiles(root, "*.icm", SearchOption.AllDirectories)
            .Select(p => new GamutProfile(ProfileService.ModeFromFileName(p), p))
            .Where(p => p.Mode != "Unknown")
            .GroupBy(p => p.Mode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => ProfileService.ModeOrder(p.Mode))
            .ToList();

        if (profiles.Count < 2)
            return new(false, $"Only {profiles.Count} recognized gamut ICC profile(s) found.", profiles, profiles.Count, false, false);

        bool native = profiles.Any(p => p.Mode.Equals("Native", StringComparison.OrdinalIgnoreCase));
        bool mhc2 = profiles.Any(p => HasTag(p.Path, "MHC2"));
        if (!native) return new(false, "Native profile is missing.", profiles, profiles.Count, false, mhc2);
        if (!mhc2) return new(false, "No profile contains an MHC2 tag.", profiles, profiles.Count, true, false);

        foreach (var p in profiles)
        {
            var id = ReadCxfIdentity(p.Path);
            if (!ValidateIdentity(id, machine, panelHardwareId, out string detail))
                return new(false, $"{p.FileName}: {detail}", profiles, profiles.Count, native, mhc2);
        }

        return new(true,
            $"VALID: {profiles.Count} Factory ICC profiles; Native present; MHC2 present; CxF identity validated.",
            profiles, profiles.Count, native, mhc2);
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
