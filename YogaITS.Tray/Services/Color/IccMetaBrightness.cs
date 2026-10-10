namespace YogaITS.Tray.Services.Color;

using System.Buffers.Binary;
using System.Text;

internal sealed record ProfileBrightnessResult(bool Ok, int Brightness, string Detail);

internal static class IccMetaBrightness
{
    public static ProfileBrightnessResult Read(string path)
    {
        try
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 132) return new(false, 0, "ICC too short.");
            uint count = ReadU32(b, 128);
            for (uint i = 0; i < count; i++)
            {
                int e = checked(132 + (int)i * 12);
                if (e + 12 > b.Length) break;
                if (Encoding.ASCII.GetString(b, e, 4) != "meta") continue;
                int off = checked((int)ReadU32(b, e + 4));
                int size = checked((int)ReadU32(b, e + 8));
                if (off < 0 || size < 16 || off > b.Length || size > b.Length - off)
                    return new(false, 0, "Invalid ICC meta tag range.");
                return ParseMeta(b.AsSpan(off, size));
            }
            return new(false, 0, "ICC has no meta tag.");
        }
        catch (Exception ex)
        {
            return new(false, 0, ex.Message);
        }
    }

    private static ProfileBrightnessResult ParseMeta(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 16 || Encoding.ASCII.GetString(tag[..4]) != "dict")
            return new(false, 0, "meta tag is not ICC dictType.");

        uint count = ReadU32(tag, 8);
        uint recordSize = ReadU32(tag, 12);
        if (recordSize < 16 || count > 1024)
            return new(false, 0, $"Invalid dict header: count={count}, recordSize={recordSize}.");

        for (uint i = 0; i < count; i++)
        {
            int ro = checked(16 + (int)(i * recordSize));
            if (ro + 16 > tag.Length) break;
            int keyOff = checked((int)ReadU32(tag, ro));
            int keyLen = checked((int)ReadU32(tag, ro + 4));
            int valOff = checked((int)ReadU32(tag, ro + 8));
            int valLen = checked((int)ReadU32(tag, ro + 12));
            if (!RangeOk(tag.Length, keyOff, keyLen) || !RangeOk(tag.Length, valOff, valLen)) continue;
            string key = DecodeUtf16Be(tag.Slice(keyOff, keyLen));
            if (!key.Equals("Brightness", StringComparison.OrdinalIgnoreCase)) continue;
            string value = DecodeUtf16Be(tag.Slice(valOff, valLen));
            if (!int.TryParse(value, out int brightness) || brightness is < 0 or > 100)
                return new(false, 0, $"Brightness value is invalid: '{value}'.");
            return new(true, brightness, $"ICC meta/dict Brightness={brightness}");
        }
        return new(false, 0, "ICC meta tag has no Brightness entry.");
    }

    private static bool RangeOk(int total, int off, int len) => off >= 0 && len >= 0 && off <= total && len <= total - off;
    private static string DecodeUtf16Be(ReadOnlySpan<byte> x)
    {
        if ((x.Length & 1) != 0) return string.Empty;
        return Encoding.BigEndianUnicode.GetString(x).TrimEnd('\0');
    }
    private static uint ReadU32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    private static uint ReadU32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o, 4));
}
