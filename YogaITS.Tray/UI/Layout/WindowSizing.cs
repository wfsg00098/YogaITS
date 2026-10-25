namespace YogaITS.Tray.UI.Layout;

internal static class WindowSizing
{
    public static int Fit(int dip, uint dpi, int workAreaPixels)
    {
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        return Math.Max(1, Math.Min((int)Math.Round(dip * scale),
            workAreaPixels - (int)Math.Round(24 * scale)));
    }
}
