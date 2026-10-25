namespace YogaITS.Tray.Demos.FluidTabs;

// Pointer ownership and grab anchoring are independent of RadioButton handlers.
internal sealed class FluidTabGesture
{
    private uint? _pointerId;
    private double _pressX, _grabFraction;
    public bool IsDragging { get; private set; }
    public int OriginalIndex { get; private set; }
    public bool Owns(uint pointerId) => _pointerId == pointerId;

    public bool Begin(uint pointerId, double x, int selected, FluidTabGeometry layout)
    {
        if (_pointerId is not null || !double.IsFinite(x)) return false;
        _pointerId = pointerId; _pressX = x; IsDragging = false; OriginalIndex = selected;
        int pressed = layout.IndexAt(x);
        _grabFraction = Math.Clamp((x - layout.Left(pressed)) / layout.Cell, 0, 1);
        return true;
    }

    public bool Move(uint pointerId, double x, FluidTabGeometry layout, out float left, out int selected)
    {
        left = 0; selected = 0;
        if (!Owns(pointerId) || !double.IsFinite(x)) return false;
        IsDragging |= Math.Abs(x - _pressX) > 4;
        if (!IsDragging) return false;
        left = (float)Math.Clamp(x - _grabFraction * layout.Cell,
            FluidTabGeometry.Padding, FluidTabGeometry.Padding + layout.Available - layout.Cell);
        selected = layout.IndexAt(left + layout.Cell / 2);
        return true;
    }

    public void End() { _pointerId = null; IsDragging = false; }
}
