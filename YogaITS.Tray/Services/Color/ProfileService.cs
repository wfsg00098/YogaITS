namespace YogaITS.Tray.Services.Color;

using System.Runtime.InteropServices;

internal sealed record GamutProfile(string Mode, string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

internal static class ProfileService
{
    internal static readonly string ColorDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color");

    public static string? GetStandardDefault(DisplayInfo d)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.ColorProfileGetDisplayDefault(
                d.ProfileScope, d.AdapterId, d.SourceId,
                NativeMethods.CPT_ICC, NativeMethods.CPST_STANDARD_DISPLAY_COLOR_MODE, out p);
            if (hr < 0 || p == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            if (p != IntPtr.Zero) NativeMethods.LocalFree(p);
        }
    }

    public static string ManagedProfileName(string mode)
    {
        string safe = new(mode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Invalid gamut mode.", nameof(mode));
        return $"YogaITS_{safe}.icm";
    }

    public static string ModeFromCurrent(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return "<none>";
        string m = ModeFromFileName(profileName);
        return m == "Unknown" ? "Custom / Unknown" : m;
    }

    public static string ModeFromFileName(string p)
    {
        string n = Path.GetFileNameWithoutExtension(p);
        if (n.EndsWith("_Native", StringComparison.OrdinalIgnoreCase)) return "Native";
        if (n.EndsWith("_sRGB", StringComparison.OrdinalIgnoreCase)) return "sRGB";
        if (n.EndsWith("_DisplayP3", StringComparison.OrdinalIgnoreCase)) return "DisplayP3";
        if (n.EndsWith("_AdobeRGB", StringComparison.OrdinalIgnoreCase)) return "AdobeRGB";
        if (n.EndsWith("_DCIP3", StringComparison.OrdinalIgnoreCase)) return "DCIP3";
        if (n.EndsWith("_REC709", StringComparison.OrdinalIgnoreCase)) return "REC709";
        if (n.EndsWith("_Default", StringComparison.OrdinalIgnoreCase)) return "Default";
        return "Unknown";
    }

    public static int ModeOrder(string m) => m.ToUpperInvariant() switch
    {
        "NATIVE" => 0,
        "SRGB" => 1,
        "DISPLAYP3" => 2,
        "ADOBERGB" => 3,
        "DCIP3" => 4,
        "REC709" => 5,
        "DEFAULT" => 6,
        _ => 99
    };

    public static bool SameFile(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
}
