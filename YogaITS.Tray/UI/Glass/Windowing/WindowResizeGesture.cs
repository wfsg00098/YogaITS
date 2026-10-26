namespace YogaITS.Tray.UI.Glass.Windowing;

// One pointer session owns all four edges. Screen pixels avoid feedback when
// resizing the left/top changes the pointer's coordinates inside the control.
internal sealed class WindowResizeGesture
{
    public bool Active { get; private set; }
    public (int X, int Y, int Width, int Height) Original { get; private set; }
    private int _hit, _startX, _startY;
    private (int Width, int Height) _minimum;

    public void Begin(int hit, int x, int y, (int X, int Y, int Width, int Height) bounds, (int Width, int Height) minimum)
    {
        if (GlassEdgePolicy.ResizeCursor(hit) == 0) throw new ArgumentOutOfRangeException(nameof(hit));
        _hit = hit; _startX = x; _startY = y;
        Original = bounds;
        _minimum = (Math.Max(1, minimum.Width), Math.Max(1, minimum.Height));
        Active = true;
    }

    public bool TryMove(int x, int y, out (int X, int Y, int Width, int Height) bounds)
    {
        bounds = default;
        if (!Active) return false;
        int dx = x - _startX, dy = y - _startY;
        int left = Original.X, right = left + Original.Width, top = Original.Y, bottom = top + Original.Height;
        if (_hit is 10 or 13 or 16) left = Math.Min(left + dx, right - _minimum.Width);
        if (_hit is 11 or 14 or 17) right = Math.Max(right + dx, left + _minimum.Width);
        if (_hit is 12 or 13 or 14) top = Math.Min(top + dy, bottom - _minimum.Height);
        if (_hit is 15 or 16 or 17) bottom = Math.Max(bottom + dy, top + _minimum.Height);
        bounds = (left, top, right - left, bottom - top);
        return true;
    }

    public void End() => Active = false;
}
