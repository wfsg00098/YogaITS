using System.Buffers.Binary;
using System.Text;

namespace YogaITS.Tray.Services.Color;

// Structural checks only: no model/mode list, CxF identity or recommended
// brightness is required. Never rewrite or repair the profile bytes.
internal static class IccCalibrationData
{
    private static readonly HashSet<string> DescriptiveTags =
        ["CxF ", "meta", "desc", "cprt", "dmnd", "dmdd", "DDPS", "tech", "view", "vued"];

    public static bool Validate(string path, out string detail)
    {
        try
        {
            if (new FileInfo(path).Length > 128L * 1024 * 1024)
                throw new InvalidDataException("ICC file exceeds the calibration package size limit.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 132 || U32(bytes, 0) != bytes.Length || Signature(bytes, 36) != "acsp")
                throw new InvalidDataException("ICC header or declared file size is invalid.");
            uint count = U32(bytes, 128);
            if (count == 0 || count > (bytes.Length - 132) / 12)
                throw new InvalidDataException("ICC tag table is incomplete.");
            int tableEnd = checked(132 + (int)count * 12);
            var tags = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                string name = Signature(bytes, entry);
                if (DescriptiveTags.Contains(name)) continue;
                uint offset = U32(bytes, entry + 4), size = U32(bytes, entry + 8);
                if (offset < tableEnd || size < 8 || offset > bytes.Length || size > bytes.Length - offset ||
                    !tags.TryAdd(name, bytes.AsSpan((int)offset, (int)size).ToArray()))
                    throw new InvalidDataException($"ICC calibration tag {name} has an invalid range or duplicate entry.");
            }
            foreach (var (name, data) in tags)
            {
                if (name is "rXYZ" or "gXYZ" or "bXYZ" or "wtpt" or "bkpt" or "lumi")
                {
                    if (data.Length < 20 || Signature(data, 0) != "XYZ ")
                        throw new InvalidDataException($"ICC {name} tristimulus data is incomplete.");
                }
                if (name is "rTRC" or "gTRC" or "bTRC" or "kTRC") ValidateCurve(data);
                if (name.StartsWith("A2B", StringComparison.Ordinal) || name.StartsWith("B2A", StringComparison.Ordinal))
                    ValidateLut(data);
            }
            if (tags.TryGetValue("MHC2", out var mhc))
            {
                ValidateMhc(mhc);
                if (!new[] { "rXYZ", "gXYZ", "bXYZ", "wtpt", "lumi" }.All(tags.ContainsKey))
                    throw new InvalidDataException("MHC2 display characterization data is incomplete.");
            }
            else if (!(new[] { "rXYZ", "gXYZ", "bXYZ", "wtpt", "rTRC", "gTRC", "bTRC" }.All(tags.ContainsKey) ||
                       tags.ContainsKey("A2B0") || tags.ContainsKey("kTRC") && tags.ContainsKey("wtpt")))
                throw new InvalidDataException("ICC contains no complete calibration or characterization data.");
            detail = "ICC calibration data is structurally complete.";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or ArgumentException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static void ValidateCurve(byte[] data)
    {
        if (data.Length < 12) throw new InvalidDataException("ICC tone curve is truncated.");
        string type = Signature(data, 0);
        if (type == "curv" && 12L + U32(data, 8) * 2L <= data.Length) return;
        if (type == "para")
        {
            int function = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2));
            int[] parameters = [1, 3, 4, 5, 7];
            if (function < parameters.Length && 12 + parameters[function] * 4 <= data.Length) return;
        }
        throw new InvalidDataException("ICC tone curve data is incomplete or invalid.");
    }

    // Microsoft MHC2 structure: offsets are relative to the tag; zero explicitly
    // denotes an identity transform. https://learn.microsoft.com/windows/win32/wcs/display-calibration-mhc
    private static void ValidateMhc(byte[] data)
    {
        if (data.Length < 36 || Signature(data, 0) != "MHC2" || U32(data, 8) > 4096)
            throw new InvalidDataException("MHC2 header is incomplete or invalid.");
        uint matrix = U32(data, 20);
        if (matrix != 0 && !Range(data, matrix, 48, 36))
            throw new InvalidDataException("MHC2 matrix is truncated.");
        for (int field = 24; field <= 32; field += 4)
        {
            uint offset = U32(data, field);
            if (offset == 0) continue;
            if (!Range(data, offset, 8L + U32(data, 8) * 4L, 36) || Signature(data, (int)offset) != "sf32")
                throw new InvalidDataException("MHC2 calibration LUT is truncated or invalid.");
        }
    }

    private static void ValidateLut(byte[] data)
    {
        string type = Signature(data, 0);
        if (type is "mft1" or "mft2")
        {
            int header = type == "mft1" ? 48 : 52;
            if (data.Length < header || data[8] == 0 || data[9] == 0 || data[10] < 2)
                throw new InvalidDataException("ICC LUT header is incomplete.");
            long grid = 1;
            for (int i = 0; i < data[8]; i++)
            {
                grid *= data[10];
                if (grid > data.Length) throw new InvalidDataException("ICC LUT grid is truncated.");
            }
            long inputs = type == "mft1" ? 256 : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(48, 2));
            long outputs = type == "mft1" ? 256 : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(50, 2));
            long size = header + (inputs * data[8] + grid * data[9] + outputs * data[9]) * (type == "mft1" ? 1 : 2);
            if (size > data.Length) throw new InvalidDataException("ICC LUT values are truncated.");
        }
        else if (type is "mAB " or "mBA ")
        {
            if (data.Length < 32) throw new InvalidDataException("ICC multi-process LUT is truncated.");
            for (int field = 12; field < 32; field += 4)
                if (U32(data, field) is var offset && offset != 0 && !Range(data, offset, field == 16 ? 48 : 8, 32))
                    throw new InvalidDataException("ICC multi-process LUT has an invalid element offset.");
        }
        // Unknown future tag types remain eligible when their declared payload
        // is present; the Windows installer remains the authority for support.
    }

    private static bool Range(byte[] data, uint offset, long size, int minimum) =>
        offset >= minimum && offset <= data.Length && size <= data.Length - offset;
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static string Signature(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);
}
