namespace YogaITS.Tray.UI.Glass.Rendering;

internal static class GlassScalePolicy
{
    // 100% retains the upstream default; the revised 200% preference is 15.
    public const double HighlightAt100 = 30;
    public const double HighlightAt200 = 15;
    public static double HighlightRange(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        scale = Math.Clamp(scale, 0.25, 8);
        // The shader's inner falloff width is proportional to range² * DPR.
        // Interpolate that physical width, not a non-linear material parameter.
        // Beyond the calibrated interval retain the nearest physical width.
        double width100 = HighlightAt100 * HighlightAt100;
        double width200 = HighlightAt200 * HighlightAt200 * 2;
        double width = width100 + (width200 - width100) * Math.Clamp(scale - 1, 0, 1);
        return Math.Sqrt(width / scale);
    }

    // Full-monitor Gaussian is applied once BEFORE the local HWND crop.
    // Neither the window glass nor cards add the old separable blur again.
    public static double BlurAmount(double scale, bool windowBackground) => 0;
}
