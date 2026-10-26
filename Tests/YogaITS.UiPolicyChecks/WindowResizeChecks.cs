using YogaITS.Tray.UI.Glass.Windowing;

internal static class WindowResizeChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
        var gesture = new WindowResizeGesture();
        foreach (int hit in Enumerable.Range(10, 8))
        {
            gesture.Begin(hit, -200, 120, (-400, -100, 600, 800), (120, 80));
            Check(gesture.TryMove(-180, 150, out var bounds), "A held pointer must update window bounds.");
            bool left = hit is 10 or 13 or 16, right = hit is 11 or 14 or 17;
            bool top = hit is 12 or 13 or 14, bottom = hit is 15 or 16 or 17;
            Check(bounds.X == -400 + (left ? 20 : 0) && bounds.Y == -100 + (top ? 30 : 0), "Moving edges follow screen coordinates on negative-coordinate monitors.");
            Check(bounds.Width == 600 + (right ? 20 : left ? -20 : 0) && bounds.Height == 800 + (bottom ? 30 : top ? -30 : 0), "Only the requested edges change dimensions.");
            gesture.End();
            Check(!gesture.TryMove(500, 500, out _), "Mouse-up or cancellation must stop every later pointer move.");
        }
        gesture.Begin(13, 0, 0, (10, 20, 600, 800), (120, 80));
        gesture.TryMove(2000, 2000, out var minimum);
        Check(minimum == (490, 740, 120, 80), "Minimum size preserves the fixed bottom/right edges.");
        gesture.End();
        gesture.Begin(11, 700, 300, (10, 20, 600, 800), (120, 80));
        gesture.TryMove(710, 300, out var restarted);
        Check(restarted == (10, 20, 610, 800), "A new drag must not inherit the previous pointer's delta.");
        gesture.End();
        return checks;
    }
}
