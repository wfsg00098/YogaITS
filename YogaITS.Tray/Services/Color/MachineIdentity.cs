namespace YogaITS.Tray.Services.Color;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

internal sealed record MachineIdentity(string MachineType, string SerialNumber, string ProductName, string Sku)
{
    public string MaskedSerial => string.IsNullOrWhiteSpace(SerialNumber) ? "<unavailable>" : "available (masked)";
}

internal static class MachineIdentityService
{
    private const uint RSMB = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint FirmwareTableProviderSignature, uint FirmwareTableID,
        byte[]? pFirmwareTableBuffer, uint BufferSize);

    public static MachineIdentity Read()
    {
        uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size < 16) throw new InvalidOperationException("SMBIOS table is unavailable.");
        var raw = new byte[checked((int)size)];
        uint got = GetSystemFirmwareTable(RSMB, 0, raw, size);
        if (got == 0 || got > (uint)raw.Length) throw new InvalidOperationException("GetSystemFirmwareTable(RSMB) failed.");
        int gotInt = checked((int)got);

        // RawSMBIOSData header: 4 x BYTE + DWORD Length, followed by SMBIOS structures.
        int pos = 8;
        string product = "", serial = "", sku = "";
        while (pos + 4 <= gotInt)
        {
            byte type = raw[pos];
            int len = raw[pos + 1];
            if (len < 4 || pos + len > gotInt) break;

            int stringsStart = pos + len;
            int end = FindStructureEnd(raw, stringsStart, gotInt);
            var strings = ReadStrings(raw, stringsStart, end);

            if (type == 1)
            {
                byte productIndex = len > 5 ? raw[pos + 5] : (byte)0;
                byte serialIndex  = len > 7 ? raw[pos + 7] : (byte)0;
                byte skuIndex     = len > 25 ? raw[pos + 25] : (byte)0;
                product = Lookup(strings, productIndex);
                serial = Lookup(strings, serialIndex);
                sku = Lookup(strings, skuIndex);
                break;
            }
            if (type == 127) break;
            pos = end;
        }

        string mt = ResolveMachineType(product, sku);
        if (string.IsNullOrWhiteSpace(mt)) throw new InvalidOperationException("Unable to resolve Lenovo machine type from SMBIOS System Information.");
        if (string.IsNullOrWhiteSpace(serial)) throw new InvalidOperationException("Unable to resolve system serial number from SMBIOS System Information.");
        return new(mt.ToUpperInvariant(), serial.Trim(), product.Trim(), sku.Trim());
    }

    private static int FindStructureEnd(byte[] b, int start, int limit)
    {
        for (int i = start; i + 1 < limit; i++)
            if (b[i] == 0 && b[i + 1] == 0) return i + 2;
        return limit;
    }

    private static List<string> ReadStrings(byte[] b, int start, int end)
    {
        var result = new List<string>();
        int p = start;
        while (p < end - 1 && b[p] != 0)
        {
            int q = p;
            while (q < end && b[q] != 0) q++;
            result.Add(Encoding.UTF8.GetString(b, p, q - p).Trim());
            p = q + 1;
        }
        return result;
    }

    private static string Lookup(List<string> strings, byte index) =>
        index == 0 || index > strings.Count ? string.Empty : strings[index - 1];

    private static string ResolveMachineType(string product, string sku)
    {
        string p = product.Trim().ToUpperInvariant();
        if (Regex.IsMatch(p, @"^[0-9A-Z]{4}$")) return p;
        var m = Regex.Match(sku ?? string.Empty, @"(?:^|_)MT_(?<mt>[0-9A-Z]{4})(?:_|$)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["mt"].Value;
        m = Regex.Match(p, @"\b(?<mt>[0-9A-Z]{4})\b");
        return m.Success ? m.Groups["mt"].Value : string.Empty;
    }
}
