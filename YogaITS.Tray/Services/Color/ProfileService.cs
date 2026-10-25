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
        if (string.IsNullOrWhiteSpace(mode) || mode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid gamut mode.", nameof(mode));
        return $"YogaITS_{mode}.icm";
    }

    public static IEnumerable<string> EnumerateProfiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".icm", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".icc", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
        : [];

    public static string ModeFromCurrent(string? profileName, IEnumerable<GamutProfile>? profiles = null)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return "<none>";
        string mode = ModeFromFileName(profileName);
        return profiles?.FirstOrDefault(profile => profile.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase))?.Mode ?? mode;
    }

    public static string ModeFromFileName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0) return Path.GetFileName(path);
        if (name.StartsWith("YogaITS_", StringComparison.OrdinalIgnoreCase)) return name[8..];
        // Lenovo package filenames use TPLCD_<panel>_<mode> or <panel>_<mode>.
        // Keep the entire remaining suffix, including underscores and its casing.
        int separator = name.IndexOf('_');
        if (name.StartsWith("TPLCD_", StringComparison.OrdinalIgnoreCase) && separator >= 0)
            separator = name.IndexOf('_', separator + 1);
        return separator >= 0 && separator + 1 < name.Length ? name[(separator + 1)..] : name;
    }

    public static int ModeOrder(string mode) => mode.ToUpperInvariant() switch
    {
        "NATIVE" => 0,
        "SRGB" => 1,
        "DISPLAYP3" => 2,
        "ADOBERGB" => 3,
        "DCIP3" => 4,
        "REC709" => 5,
        "DEFAULT" => 6,
        "DICOM_DIM" => 7,
        "DICOM_OFFICE" => 8,
        _ => 99
    };

    public static bool SameFile(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
}
