using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;
using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using LiquidGlassWinUI;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.UI;

namespace YogaITS.Tray.UI.Glass.Rendering;
// Shared glass shader/factory pool; a separate native shape
// supplies the rounded opaque/translucent fallback without background capture.
internal sealed class GlassTabSurface : IDisposable
{
    private readonly Compositor _compositor;
    private readonly bool _isTrack;
    private readonly SpriteVisual _glassVisual;
    private readonly ContainerVisual _deformed;
    private readonly ShapeVisual _nativeVisual;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionSpriteShape _shape;
    private readonly CompositionColorBrush _color;
    private readonly DropShadow _shadow;
    private readonly ShapeVisual _shadowMaskVisual;
    private readonly CompositionSpriteShape _shadowMaskShape;
    private readonly CompositionColorBrush _shadowMaskColor;
    private readonly CompositionVisualSurface _shadowMaskSurface;
    private readonly CompositionSurfaceBrush _shadowMaskBrush;
    private readonly Dictionary<string, ScalarKeyFrameAnimation> _bounded = new();
    private LiquidGlassBrush? _glass;
    private readonly GlassMotionFeedback _feedback;
    private TabFrame _target;
    private readonly FloatingTabKinetics _kinetics = new();
    private readonly ScalarKeyFrameAnimation _inertia;
    private ScalarKeyFrameAnimation? _plannedInertia;
    private bool _stopPlanned;
    private bool _inertiaEnabled;
    private Vector2 _shapedVelocity;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public ContainerVisual Visual { get; }
    public CompositionPropertySet Frame { get; }

    public GlassTabSurface(Compositor compositor, bool track = false, bool floating = false)
    {
        _compositor = compositor;
        _isTrack = track;
        Frame = compositor.CreatePropertySet();
        foreach (string key in new[] { "X", "Y", "Width", "Height", "LabelWidth", "Center", "CenterY", "RestWidth", "RestHeight", "DestinationX", "DestinationY" })
            Frame.InsertScalar(key, 1);
        foreach (string key in new[] { "StrainX", "StrainY", "StrainXY", "RenderX", "RenderY" }) Frame.InsertScalar(key, 0);
        foreach (string key in new[] { "DirectionX", "DirectionY", "NormalX", "NormalY", "ProjectedX", "ProjectedY", "Extension", "Remaining", "FrontAnchored", "CenterAnchored", "Forward" }) Frame.InsertScalar(key, 0);
        Frame.InsertScalar("CornerLimit", track ? 30 : 65536);
        Frame.InsertScalar("Left", 0);
        Frame.InsertScalar("Right", 1);
        Visual = compositor.CreateContainerVisual();
        _deformed = compositor.CreateContainerVisual();
        _deformed.RelativeSizeAdjustment = Vector2.One;
        Visual.Children.InsertAtTop(_deformed);
        _feedback = new GlassMotionFeedback(Visual, track, floating, Frame);
        Bind(Frame, "X", "F.Center-F.Width/2");
        Bind(Frame, "Y", "F.CenterY-F.Height/2");
        Bind(Frame, "Width", "Max(1,F.RestWidth)");
        Bind(Frame, "Height", "Max(1,F.RestHeight)");
        Bind(Frame, "RenderX", GlassStrain.LimitExpression("F.StrainX"));
        Bind(Frame, "RenderY", GlassStrain.LimitExpression("F.StrainY"));
        const string xx = "F.RenderX", yy = "F.RenderY", xy = "Clamp(F.StrainXY,-0.24,0.24)";
        // Transform the support normal through the outer pressure scale, so
        // the wall is measured from the visible contour, not its logical center.
        Bind(Frame, "NormalX", "F.DirectionX*V.Scale.X");
        Bind(Frame, "NormalY", "F.DirectionY*V.Scale.Y");
        Bind(Frame, "ProjectedX", "(1+" + xx + ")*F.NormalX+(" + xy + ")*F.NormalY");
        Bind(Frame, "ProjectedY", "(" + xy + ")*F.NormalX+(1+" + yy + ")*F.NormalY");
        // Support of a capsule = its straight core plus a circular end cap.
        const string radius = "(Min(F.Width,F.Height)/2)";
        Bind(Frame, "Extension", "(F.Width/2-" + radius + ")*(Abs(F.ProjectedX)-Abs(F.NormalX))+" +
            "(F.Height/2-" + radius + ")*(Abs(F.ProjectedY)-Abs(F.NormalY))+" + radius +
            "*(Sqrt(F.ProjectedX*F.ProjectedX+F.ProjectedY*F.ProjectedY)-Sqrt(F.NormalX*F.NormalX+F.NormalY*F.NormalY))");
        Bind(Frame, "Remaining", "Max(0,(F.DestinationX-F.Center)*F.DirectionX+" +
            "(F.DestinationY-F.CenterY)*F.DirectionY)");
        ResetAnchor();
        Bind(Frame, "Forward", "F.CenterAnchored>0.5 ? 0 : (F.FrontAnchored>0.5 ? F.Remaining-F.Extension : Abs(F.Extension))/" +
            "Max(0.001,F.DirectionX*F.NormalX+F.DirectionY*F.NormalY)");
        // Stage one fixes the rear while stretching forward. First contact
        // permanently switches to the leading plane for this movement/recoil.
        // No boundary or anchor is imposed on the perpendicular strain.
        Bind(_deformed, "TransformMatrix", "Matrix4x4(1+" + xx + "," + xy + ",0,0," + xy + ",1+" + yy +
            ",0,0,0,0,1,0,-(" + xx + ")*F.Width/2-(" + xy + ")*F.Height/2+F.DirectionX*F.Forward," +
            "-(" + xy + ")*F.Width/2-(" + yy + ")*F.Height/2+F.DirectionY*F.Forward,0,1)");
        _inertia = CreateInertia();
        _glassVisual = compositor.CreateSpriteVisual();
        _glassVisual.RelativeSizeAdjustment = Vector2.One;
        _nativeVisual = compositor.CreateShapeVisual();
        _nativeVisual.RelativeSizeAdjustment = Vector2.One;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _shape = compositor.CreateSpriteShape(_geometry);
        _color = compositor.CreateColorBrush();
        _shape.FillBrush = _color;
        _nativeVisual.Shapes.Add(_shape);
        _deformed.Children.InsertAtBottom(_nativeVisual);
        _deformed.Children.InsertAtTop(_glassVisual);
        Bind(Visual, "Offset", "Vector3(F.X,F.Y,0)");
        Bind(Visual, "Size", "Vector2(Max(F.Width,1),Max(F.Height,1))");
        Bind(_geometry, "Size", "Vector2(Max(F.Width,1),Max(F.Height,1))");
        Bind(_geometry, "CornerRadius", "Vector2(Min(Min(F.Width,F.Height)/2,F.CornerLimit),Min(Min(F.Width,F.Height)/2,F.CornerLimit))");
        // A backdrop-effect sprite has no dependable inherited alpha mask
        // before its first invalidation. Use an independent opaque contour,
        // driven by the same dimensions/radius, even before any pointer input.
        _shadowMaskVisual=compositor.CreateShapeVisual();
        _shadowMaskColor=compositor.CreateColorBrush(Colors.White);
        _shadowMaskShape=compositor.CreateSpriteShape(_geometry);
        _shadowMaskShape.FillBrush=_shadowMaskColor;
        _shadowMaskVisual.Shapes.Add(_shadowMaskShape);
        Bind(_shadowMaskVisual,"Size","Vector2(Max(F.Width,1),Max(F.Height,1))");
        _shadowMaskSurface=compositor.CreateVisualSurface();
        _shadowMaskSurface.SourceVisual=_shadowMaskVisual;
        Bind(_shadowMaskSurface,"SourceSize","Vector2(Max(F.Width,1),Max(F.Height,1))");
        _shadowMaskBrush=compositor.CreateSurfaceBrush(_shadowMaskSurface);
        _shadowMaskBrush.Stretch=CompositionStretch.Fill;
        _shadow = compositor.CreateDropShadow();
        _shadow.Color = Colors.Black;
        _shadow.Opacity = 0.16f;
        _shadow.BlurRadius = 18;
        _shadow.Offset = new(0, 8, 0);
        _shadow.Mask=_shadowMaskBrush;
        _shadow.SourcePolicy = CompositionDropShadowSourcePolicy.Default;
        _glassVisual.Shadow = _shadow;
    }

    public void Bind(CompositionObject target, string property, string expression)
    {
        using var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("F", Frame);
        animation.SetReferenceParameter("V", Visual);
        target.StartAnimation(property, animation);
    }

    private void ResetAnchor()
    {
        Frame.StopAnimation("FrontAnchored");
        Frame.InsertScalar("FrontAnchored", 0);
        // CurrentValue retains contact across frames. Centered drag/fallback
        // clears it; restart only for a genuinely new trajectory.
        Bind(Frame, "FrontAnchored", "F.CenterAnchored>0.5 || F.DirectionX*F.DirectionX+F.DirectionY*F.DirectionY<0.5 ? 0 : " +
            "Max(this.CurrentValue,F.Remaining<=F.Extension+Abs(F.Extension) ? 1 : 0)");
    }

    // Render-only entry for the shared four-edge motion controller. The existing
    // keyframe engine is untouched; no per-frame animation/template is created.
    public void SetPhysicsFrame(TabFrame frame, Vector2 restSize)
    {
        Frame.InsertScalar("Center", frame.Center);
        Frame.InsertScalar("CenterY", frame.Y + frame.Height / 2);
        Frame.InsertScalar("RestWidth", frame.Width);
        Frame.InsertScalar("RestHeight", frame.Height);
        _feedback.SetRestSize(restSize.X, restSize.Y);
        if (_isTrack && _glass is not null)
            _glass.ShapeRadius = Math.Min(1, 60 / Math.Max(1, Math.Min(frame.Width, frame.Height)));
    }

    public TimeSpan DurationTo(TabFrame frame) => FloatingTabSpring.MovementDuration(
        new Vector2(frame.Center, frame.Y + frame.Height / 2) - _kinetics.PositionAt(Now));

    public void SetFrame(TabFrame frame, bool animate, bool dragging = false,
        CompositionScopedBatch? positionBatch = null, TimeSpan? movementDuration = null)
    {
        // Drag samples are direct assignments even if a caller requests animation.
        // Stop both edge animations before writing the latest pointer position.
        animate &= !dragging;
        double now = Now;
        var center = new Vector2(frame.Center, frame.Y + frame.Height / 2);
        Frame.InsertScalar("DestinationX", center.X);
        Frame.InsertScalar("DestinationY", center.Y);
        Frame.InsertScalar("CenterAnchored", dragging ? 1 : 0);
        var duration = animate ? movementDuration ?? FloatingTabSpring.MovementDuration(center - _kinetics.PositionAt(now)) : TimeSpan.Zero;
        bool move = animate && duration > TimeSpan.Zero;
        // Long material recovery must not postpone the position-arrival event.
        positionBatch?.Suspend();
        try
        {
            if (!dragging) ResetAnchor();
            if (!dragging) _feedback.ScheduleShrink(duration, animate);
            if (animate || dragging)
            {
                // Keep the position model consistent even without glass inertia.
                var change = _kinetics.MoveTo(center, now, animate, duration);
                if (_inertiaEnabled) Kick(change, move ? duration : null);
                else ResetDeformation();
            }
            else
            {
                _kinetics.Reset(center, now);
                ResetDeformation();
            }
        }
        finally { positionBatch?.Resume(); }
        _target = frame;
        Frame.InsertScalar("RestWidth", frame.Width);
        Frame.InsertScalar("RestHeight", frame.Height);
        _feedback.SetRestSize(frame.Width, frame.Height);
        Set("Center", frame.Center);
        Set("Left", frame.X);
        Set("Right", frame.X + frame.Width);
        Set("CenterY", frame.Y + frame.Height / 2);
        void Set(string key, float value)
        {
            if (!move || key is "Left" or "Right")
            {
                Frame.StopAnimation(key);
                Frame.InsertScalar(key, value);
                return;
            }

            if (!_bounded.TryGetValue(key, out var finite))
            {
                finite = _compositor.CreateScalarKeyFrameAnimation();
                finite.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                _bounded.Add(key, finite);
            }
            finite.SetScalarParameter("Target", value);
            using var linear = _compositor.CreateLinearEasingFunction();
            finite.InsertKeyFrame(1, value, linear);
            finite.Duration = duration;
            Frame.StartAnimation(key, finite);
        }
    }

    public TabFrame Target => _target;
    internal LiquidGlassBrush? Brush => _glass;
    internal GlassSurfaceOverlay CreateOverlayVisual() => new(Visual, _deformed, _glassVisual);
    internal (bool Animating, bool RecoveryScheduled) PressureState => _feedback.PressureState;
    public bool TryContinueFrame(TabFrame frame, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (Math.Abs(frame.Width - _target.Width) > 0.01f || Math.Abs(frame.Height - _target.Height) > 0.01f) return false;
        return _kinetics.TryContinue(new(frame.Center, frame.Y + frame.Height / 2), Now, out remaining);
    }

    public void AlignShrinkToRemaining(TimeSpan remaining) => _feedback.ScheduleShrink(remaining, true);

    public void SetMotion(bool pressed, bool enabled, TimeSpan? arrival = null)
    {
        _inertiaEnabled = enabled;
        if (!enabled)
        {
            ResetDeformation();
        }
        _feedback.SetActive(pressed, enabled, arrival);
    }

    private void ResetDeformation()
    {
        _shapedVelocity = Vector2.Zero;
        _stopPlanned = false;
        Frame.InsertScalar("CenterAnchored", 0);
        Frame.InsertScalar("DirectionX", 0);
        Frame.InsertScalar("DirectionY", 0);
        foreach (string key in new[] { "StrainX", "StrainY", "StrainXY" })
        {
            Frame.StopAnimation(key);
            Frame.InsertScalar(key, 0);
        }
    }

    private ScalarKeyFrameAnimation CreateInertia(TimeSpan? stopAt = null)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = (stopAt ?? TimeSpan.Zero) + FloatingTabSpring.InertiaDuration;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        using var linear = _compositor.CreateLinearEasingFunction();
        if (stopAt is TimeSpan stop)
            for (int i = 1; i <= FloatingTabSpring.Samples; i++)
                Add(TimeSpan.FromTicks(stop.Ticks * i / FloatingTabSpring.Samples));
        for (int i = 1; i <= FloatingTabSpring.Samples; i++)
            Add((stopAt ?? TimeSpan.Zero) + TimeSpan.FromTicks(FloatingTabSpring.InertiaDuration.Ticks * i / FloatingTabSpring.Samples));
        return animation;

        void Add(TimeSpan elapsed)
        {
            var weights = FloatingTabSpring.InertiaWeights(elapsed, stopAt);
            string N(float value) => value.ToString("0.########", CultureInfo.InvariantCulture);
            animation.InsertExpressionKeyFrame((float)(elapsed.TotalSeconds / animation.Duration.TotalSeconds),
                "this.StartingValue*(" + N(weights.X) + ")+Target*(" + N(weights.Y) +
                ")+Kick*(" + N(weights.Z) + ")+StopKick*(" + N(weights.W) + ")", linear);
        }
    }

    private void Kick(VelocityChange change, TimeSpan? stopAt = null)
    {
        var size = new Vector2(Math.Max(1, _target.Width), Math.Max(1, _target.Height));
        // Accumulate small speed/direction changes against the last applied shape.
        // A final zero always restores rest, even after very slow motion.
        bool leavingPlan = _stopPlanned && stopAt is null;
        if (!leavingPlan && !new VelocityChange(_shapedVelocity, change.Current).Significant(size) && !change.Significant(size)) return;
        var reference = change.Current.LengthSquared() > 0.001f ? change.Current
            : change.Previous.LengthSquared() > 0.001f ? change.Previous : _shapedVelocity;
        var direction = reference.LengthSquared() > 0.001f ? Vector2.Normalize(reference) : Vector2.Zero;
        Frame.InsertScalar("DirectionX", direction.X);
        Frame.InsertScalar("DirectionY", direction.Y);
        _shapedVelocity = change.Current;
        var target = change.Stretch(size);
        var impulse = change.Impulse(size);
        var stopping = new VelocityChange(change.Current, Vector2.Zero).Impulse(size);
        _stopPlanned = stopAt is not null;
        var animation = _inertia;
        if (_stopPlanned)
        {
            _plannedInertia?.Dispose();
            animation = _plannedInertia = CreateInertia(stopAt);
        }
        Start("StrainX", target.X, impulse.X, stopping.X);
        Start("StrainY", target.Y, impulse.Y, stopping.Y);
        Start("StrainXY", target.XY, impulse.XY, stopping.XY);
        void Start(string key, float value, float kick, float stopKick)
        {
            animation.SetScalarParameter("Target", value);
            animation.SetScalarParameter("Kick", kick);
            animation.SetScalarParameter("StopKick", stopKick);
            Frame.StartAnimation(key, animation);
        }
    }

    public void StopMotion(bool animate = true)
    {
        var change = _kinetics.Stop(Now);
        if (!animate) ResetDeformation();
        else if (_inertiaEnabled)
        {
            if (_stopPlanned)
            {
                // The compositor is already playing the planned stopping phase.
                _stopPlanned = false;
                _shapedVelocity = Vector2.Zero;
            }
            else Kick(change);
        }
    }

    public void SetMaterial(bool glass, Color tint, Color fallback, float scale, bool shadow = true, bool updateTint = true)
    {
        _color.Color = fallback;
        _nativeVisual.Opacity = glass ? 0 : 1;
        _glassVisual.Opacity = glass ? 1 : 0;
        _shadow.Opacity = shadow ? 0.16f : 0;
        if (!glass)
        {
            _inertiaEnabled = false;
            ResetDeformation();
            _feedback.SetActive(false, false);
            _feedback.BindGlass(null);
            _glassVisual.Brush = null;
            _glass?.DisconnectFromVisual();
            _glass = null;
            return;
        }

        if (_glass is null)
        {
            _glass = new LiquidGlassBrush
            {
                ShapeRadius = 1,
                ShapeRoundness = 2,
                ShapeInset = 0,
                BlurAmount = 0,
                BackgroundBlurAmount = 0,
                FillWindowBackground = 0
            };
            _glassVisual.Brush = _glass.ConnectToVisual();
        }

        Frame.TryGetScalar("RestWidth", out float physicalWidth);
        Frame.TryGetScalar("RestHeight", out float physicalHeight);
        _glass.ShapeRadius = _isTrack ? Math.Min(1, 60 / Math.Max(1, Math.Min(physicalWidth, physicalHeight))) : 1;
        _glass.Dpr = scale;
        _glass.RefFresnelRange = _glass.GlareRange = GlassScalePolicy.HighlightRange(scale);
        if (updateTint) _glass.SetTint(tint.R, tint.G, tint.B, tint.A / 255.0);
        _feedback.BindGlass(_glass);
    }

    public void Dispose()
    {
        _feedback.Dispose();
        ResetDeformation();
        _inertia.Dispose();
        _plannedInertia?.Dispose();
        Frame.StopAnimation("X");
        Frame.StopAnimation("Y");
        Frame.StopAnimation("Width");
        Frame.StopAnimation("Height");
        _deformed.StopAnimation("TransformMatrix");
        foreach (string key in _bounded.Keys)
            Frame.StopAnimation(key);
        _glassVisual.Brush = null;
        _glass?.DisconnectFromVisual();
        _glass = null;
        Visual.Children.RemoveAll();
        _deformed.Children.RemoveAll();
        Visual.StopAnimation("Offset");
        Visual.StopAnimation("Size");
        _geometry.StopAnimation("Size");
        _geometry.StopAnimation("CornerRadius");
        _glassVisual.Shadow=null;
        _shadow.Mask=null;
        _shadowMaskSurface.StopAnimation("SourceSize");
        _shadowMaskSurface.SourceVisual=null;
        _shadowMaskVisual.StopAnimation("Size");
        _shadowMaskVisual.Shapes.Clear();
        _shadowMaskBrush.Dispose();
        _shadowMaskSurface.Dispose();
        _shadowMaskShape.Dispose();
        _shadowMaskColor.Dispose();
        _shadowMaskVisual.Dispose();
        _glassVisual.Dispose();
        _nativeVisual.Dispose();
        _deformed.Dispose();
        _shape.Dispose();
        _geometry.Dispose();
        _color.Dispose();
        _shadow.Dispose();
        Visual.Dispose();
        Frame.Dispose();
        foreach (var finite in _bounded.Values)
            finite.Dispose();
    }
}
