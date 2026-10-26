namespace YogaITS.Tray.UI.Glass.Windowing;

internal static class GlassEdgePolicy
{
    // These are backgrounds, not floating shapes with an exposed outer gutter.
    public const double ShapeInset = 0;
    public const double WindowRadiusDip = 8;
    public static double CardRadius(double height) => Math.Max(0, height) / 2;
    public static double ShapeRadius(double shorterSide, double radius) =>
        Math.Clamp(2 * radius / Math.Max(1, shorterSide - 2 * ShapeInset), 0, 1);

    // Standard hit testing outside the HWND no longer supplies resize grips
    // when its client fills the complete frame. Reserve 4 DIP on the inside.
    public static nint ResizeHitTest(int x, int y, int width, int height, int grip, nint nativeResult)
    {
        if (x < 0 || y < 0 || x >= width || y >= height ||
            (nativeResult != 0 && nativeResult != 1 && nativeResult != 2 && nativeResult != 18 &&
             ((long)nativeResult < 10 || (long)nativeResult > 17))) return nativeResult;
        bool left = x < grip, right = x >= width-grip;
        bool top = y < grip, bottom = y >= height-grip;
        if (top) return left ? 13 : right ? 14 : 12;
        if (bottom) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : nativeResult;
    }

    // Correct only a full-width WinUI site shifted down by one physical pixel.
    // Its bottom may be clipped to the client or extend one pixel beyond it.
    // Never reposition partial/custom hosts, normal title bars or collapsed sites.
    public static bool HasReservedTopRow(int width, int height, int left, int top, int right, int bottom) =>
        width > 0 && height > 1 && left == 0 && top == 1 && right == width &&
        (bottom == height || (long)bottom == (long)height + 1);
}
