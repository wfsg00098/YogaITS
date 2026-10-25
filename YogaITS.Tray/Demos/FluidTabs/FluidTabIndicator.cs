using System.Numerics;
using Microsoft.UI.Composition;

namespace YogaITS.Tray.Demos.FluidTabs;

// Two springs drive the edges of ONE refracting capsule. The leading edge
// responds first; the tail catches up. No per-frame XAML layout or UI timer.
internal sealed class FluidTabIndicator : IDisposable
{
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _motion;
    private readonly RectangleClip _nativeClip;
    private readonly FluidTabMotionState _state = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private CompositionScopedBatch? _batch;
    private bool _disposed;
    public bool IsMoving => _state.IsMoving;
    public event Action<bool>? MovingChanged;
    public bool NativeFallback
    {
        set
        {
            Visual.Clip = value ? _nativeClip : null;
            _motion.InsertScalar("Native", value ? 1 : 0);
        }
    }
    private FluidTabGeometry _layout;
    private float _target;
    public SpriteVisual Visual { get; }

    public FluidTabIndicator(Compositor compositor)
    {
        _compositor = compositor;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _motion = compositor.CreatePropertySet();
        _motion.InsertScalar("Left", 4); _motion.InsertScalar("Right", 5);
        _motion.InsertScalar("Rest", 1); _motion.InsertScalar("Available", 4);
        _motion.InsertScalar("Height", 1); _motion.InsertScalar("Top", 4); _motion.InsertScalar("Native", 1);
        Visual = compositor.CreateSpriteVisual();
        _nativeClip = compositor.CreateRectangleClip();
        using var right = compositor.CreateExpressionAnimation("V.Size.X");
        right.SetReferenceParameter("V", Visual); _nativeClip.StartAnimation("Right", right);
        using var bottom = compositor.CreateExpressionAnimation("V.Size.Y");
        bottom.SetReferenceParameter("V", Visual); _nativeClip.StartAnimation("Bottom", bottom);
        const string width = "Min(P.Available, Max(P.Rest * 0.72, P.Right - P.Left))";
        const string height = "P.Height";
        using var size = compositor.CreateExpressionAnimation("Vector2(" + width + ", " + height + ")");
        size.SetReferenceParameter("P", _motion);
        Visual.StartAnimation("Size", size);
        using var offset = compositor.CreateExpressionAnimation(
            "Vector3(Clamp(P.Left, 4, 4 + P.Available - (" + width + ")), P.Top + (P.Height - (" + height + ")) / 2, 0)");
        offset.SetReferenceParameter("P", _motion);
        Visual.StartAnimation("Offset", offset);
    }

    public void Configure(FluidTabGeometry layout, int selected)
    {
        _layout = layout;
        // RectangleClip radius setters are not animatable in the bundled SDK.
        // Constant capsule height; compositor feedback inflates glass around its center.
        var radius = new Vector2(layout.PillHeight / 2);
        _nativeClip.TopLeftRadius = _nativeClip.TopRightRadius = _nativeClip.BottomLeftRadius = _nativeClip.BottomRightRadius = radius;
        _motion.InsertScalar("Rest", layout.Cell);
        _motion.InsertScalar("Available", layout.Available);
        _motion.InsertScalar("Height", layout.PillHeight);
        _motion.InsertScalar("Top", (layout.Height - layout.PillHeight) / 2);
        MoveTo(layout.Left(selected), false);
    }

    public void Select(int selected, bool animate) => MoveTo(_layout.Left(selected), animate);
    public void DragTo(float left, bool animate) => MoveTo(left, animate, true);

    private void MoveTo(float left, bool animate, bool dragging = false)
    {
        if (_disposed || (animate && Math.Abs(left - _target) < 0.01f)) return;
        bool rightward = left >= _target;
        _target = left;
        bool wasMoving = IsMoving;
        int revision = _state.Begin(animate);
        ClearBatch();
        if (wasMoving != IsMoving) MovingChanged?.Invoke(IsMoving);
        if (animate)
        {
            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _batch = batch;
            batch.Completed += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || !ReferenceEquals(_batch, batch)) return;
                _batch = null; batch.Dispose();
                if (_state.Complete(revision)) MovingChanged?.Invoke(false);
            });
            try { Edges(); }
            finally { batch.End(); }
        }
        else Edges();

        void Edges()
        {
            Edge("Left", left, !rightward);
            Edge("Right", left + _layout.Cell, rightward);
        }
        void Edge(string key, float value, bool leading)
        {
            if (!animate)
            {
                _motion.StopAnimation(key);
                _motion.InsertScalar(key, value);
                return;
            }
            using var spring = _compositor.CreateSpringScalarAnimation();
            spring.FinalValue = value;
            spring.DampingRatio = dragging ? 0.86f : 0.76f;
            spring.Period = FluidTabMotionState.Period(leading, dragging);
            spring.DelayTime = TimeSpan.FromMilliseconds(dragging ? 0 : 18);
            _motion.StartAnimation(key, spring);
        }
    }

    private void ClearBatch()
    {
        var batch = _batch; _batch = null;
        batch?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _state.Begin(false); ClearBatch();
        Visual.StopAnimation("Size"); Visual.StopAnimation("Offset");
        _motion.StopAnimation("Left"); _motion.StopAnimation("Right");
        Visual.Brush = null;
        Visual.Clip = null;
        Visual.Dispose(); _nativeClip.Dispose(); _motion.Dispose();
    }
}
