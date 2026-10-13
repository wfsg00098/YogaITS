namespace YogaITS.Tray.Services.Color;

internal enum ColorGamutBackend { None, Icc, Tcon }

internal static class ColorBackendPolicy
{
    public static ColorGamutBackend Select(bool tconSupported, bool iccValidated, bool tconAbsent, bool iccEligible) =>
        tconSupported ? ColorGamutBackend.Tcon :
        iccEligible && (iccValidated || tconAbsent) ? ColorGamutBackend.Icc : ColorGamutBackend.None;

    public static bool SupportsTconMode(int ability, byte raw) =>
        (ability & 0x08) != 0 && (ability & ModeBit(raw)) != 0;

    public static int ModeBit(byte raw) => raw switch { 0x04 => 0x100, 0x06 => 0x200, 0x08 => 0x400, _ => 0 };
    public static string ModeId(byte raw) => raw switch
    {
        0x04 => "TconP3", 0x06 => "TconSrgb", 0x08 => "TconAdobeRgb", _ => "未知"
    };
    public static byte ModeRaw(string mode) => mode switch
    {
        "TconP3" => 0x04, "TconSrgb" => 0x06, "TconAdobeRgb" => 0x08,
        _ => throw new InvalidOperationException("该选项不是可用的 TCON 色域。")
    };
}
