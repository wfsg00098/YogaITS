namespace YogaITS.Tray.UI.Glass.Rendering;

// CPU reference for the small GPU conversion shader, not a pixel processing path.
// Native DISPLAYCONFIG_SDR_WHITE_LEVEL is already divided by 1000 (80-nit units).
internal static class HdrToneMappingPolicy
{
    public const double Knee = 0.75;
    public static double WhiteScale(double value) => double.IsFinite(value) && value is >= 0.1 and <= 100 ? value : 1;
    public static double Shoulder(double value)
    {
        if (!double.IsFinite(value)) return value > 0 ? 1 : 0;
        value = Math.Max(0, value);
        return value <= Knee ? value : Knee + (1 - Knee) * (1 - Math.Exp(-(value - Knee) / (1 - Knee)));
    }
    public static double EncodeSrgb(double linear) => linear <= 0.0031308
        ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
    public static (double Red, double Green, double Blue) Map(double red, double green, double blue, double whiteScale)
    {
        double scale = WhiteScale(whiteScale);
        double Channel(double value) => (double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 65504)) / scale;
        red = Channel(red);
        green = Channel(green);
        blue = Channel(blue);
        double peak = Math.Max(red, Math.Max(green, blue));
        double ratio = peak > Knee ? Shoulder(peak) / peak : 1;
        return (EncodeSrgb(red * ratio), EncodeSrgb(green * ratio), EncodeSrgb(blue * ratio));
    }
}
