using System.Numerics;

namespace YogaITS.Tray.Demos.SwiftTabs;

// A pointer session owns only a visual preview until Release. No setting,
// RadioButton state or activation event changes while the pointer is held.
internal sealed class FloatingTabGesture
{
    private SwiftTabLayout? _layout;
    private uint? _pointer;
    private Vector2 _press;
    public int ConfirmedIndex { get; private set; }
    public int PreviewIndex { get; private set; }
    public bool IsDragging { get; private set; }
    public TabFrame PreviewFrame { get; private set; }

    public bool Begin(uint pointer, double x, int selected, SwiftTabLayout layout) =>
        Begin(pointer, new Vector2((float)x, layout.Capsule(selected).CenterY), selected, layout);

    public bool Begin(uint pointer, Vector2 point, int selected, SwiftTabLayout layout)
    {
        if (_pointer is not null || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        _pointer = pointer;
        _layout = layout;
        _press = point;
        ConfirmedIndex = PreviewIndex = Math.Clamp(selected, 0, layout.Items.Length - 1);
        IsDragging = false;
        UpdatePreview(point);
        return true;
    }

    public bool Move(uint pointer, double x, out TabFrame frame) =>
        Move(pointer, new Vector2((float)x, _press.Y), out frame);

    public bool Move(uint pointer, Vector2 point, out TabFrame frame)
    {
        frame = default;
        if (_pointer != pointer || _layout is null || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        var delta = point - _press;
        IsDragging |= _layout.Rows > 1 ? delta.LengthSquared() > 16 : Math.Abs(delta.X) > 4;
        if (!IsDragging) return false;
        UpdatePreview(point);
        frame = PreviewFrame;
        return true;
    }

    private void UpdatePreview(Vector2 point)
    {
        var first = _layout!.Capsule(0);
        float leftEdge = _layout.Items.Min(frame => frame.X);
        float rightEdge = _layout.Items.Max(frame => frame.X + frame.Width);
        float topEdge = _layout.Items.Min(frame => frame.Y);
        float bottomEdge = _layout.Items.Max(frame => frame.Y + frame.Height);
        if (_layout.Rows == 1)
        {
            float left = Math.Clamp(point.X - first.Width / 2, leftEdge, Math.Max(leftEdge, rightEdge - first.Width));
            PreviewFrame = first with { X = left };
            PreviewIndex = _layout.Hit(PreviewFrame.Center);
            return;
        }
        // The final full-width option owns its whole hit rectangle.
        PreviewIndex = _layout.Hit(point.X, point.Y);
        var item = _layout.Capsule(PreviewIndex);
        PreviewFrame = item with
        {
            X = Math.Clamp(point.X - item.Width / 2, leftEdge, Math.Max(leftEdge, rightEdge - item.Width)),
            Y = Math.Clamp(point.Y - item.Height / 2, topEdge, Math.Max(topEdge, bottomEdge - item.Height))
        };
    }

    public bool Release(uint pointer, double x, out int selected) =>
        Release(pointer, new Vector2((float)x, _press.Y), out selected);

    public bool Release(uint pointer, Vector2 point, out int selected)
    {
        selected = ConfirmedIndex;
        if (_pointer != pointer || _layout is null || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
        Move(pointer, point, out _);
        selected = IsDragging ? PreviewIndex : _layout.Hit(point.X, _layout.Rows > 1 ? point.Y : _press.Y);
        ConfirmedIndex = selected;
        Cancel();
        return true;
    }

    public void Cancel()
    {
        _pointer = null;
        _layout = null;
        PreviewIndex = ConfirmedIndex;
        IsDragging = false;
    }
}
