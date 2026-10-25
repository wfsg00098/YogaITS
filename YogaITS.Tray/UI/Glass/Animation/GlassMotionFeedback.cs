using LiquidGlassWinUI;
using Microsoft.UI.Composition;

namespace YogaITS.Tray.UI.Glass.Animation;

// Compositor-only inflation and optical stretch shared by the tab demos.
// Rest dimensions are logical DIPs; DPI conversion stays inside the material.
internal sealed class GlassMotionFeedback : IDisposable
{
    private readonly Visual _visual;
    private readonly CompositionPropertySet _motion;
    private readonly bool _track;
    private readonly bool _floating;
    private LiquidGlassBrush? _glass;
    private bool _active;
    private float _restWidth=float.NaN,_restHeight=float.NaN;
    private bool _enabled;
    private bool _recovering;
    internal (bool Animating, bool RecoveryScheduled) PressureState =>
        (_motion.TryGetAnimationController("Phase") is not null, _recovering);
    private readonly CompositionPropertySet? _deformation;

    public GlassMotionFeedback(Visual visual, bool track = false, bool floating = false, CompositionPropertySet? deformation = null)
    {
        _visual = visual;
        _track = track;
        _floating = floating;
        _deformation = deformation;
        _motion = visual.Compositor.CreatePropertySet();
        _motion.InsertScalar("Pulse", 0);
        if (floating)
        {
            _motion.InsertVector4("Phase", FloatingTabPressure.Rest);
            using var pressure = visual.Compositor.CreateExpressionAnimation(FloatingTabPressure.PulseExpression("M.Phase"));
            pressure.SetReferenceParameter("M", _motion);
            _motion.StartAnimation("Pulse", pressure);
        }
        _motion.InsertScalar("RestWidth", 1);
        _motion.InsertScalar("RestHeight", 1);
        Bind("CenterPoint", "Vector3(V.Size.X/2,V.Size.Y/2,0)");
        Bind("Scale", track
            ? "Vector3(1,1,1)"
            : floating ? "Vector3(1+M.Pulse*0.10,1+M.Pulse*0.42,1)"
            : "Vector3(1+M.Pulse*0.045,1+M.Pulse*0.12,1)");
    }

    private void Bind(string property, string expression)
    {
        using var animation = _visual.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("M", _motion);
        animation.SetReferenceParameter("V", _visual);
        _visual.StartAnimation(property, animation);
    }

    public void SetRestWidth(float width)
    {
        width=Math.Max(1,width);
        if(_restWidth==width) return;
        _restWidth=width; _motion.InsertScalar("RestWidth",width);
    }
    public void SetRestSize(float width, float height)
    {
        SetRestWidth(width);
        height=Math.Max(1,height);
        if(_restHeight!=height) { _restHeight=height; _motion.InsertScalar("RestHeight",height); }
    }

    public void SetActive(bool active, bool enabled, TimeSpan? arrival = null)
    {
        if (_floating)
        {
            bool available = enabled && !_track;
            bool pressed = active && available;
            bool changed = pressed != _active || available != _enabled;
            _enabled = available;
            _active = pressed;
            if (!available)
            {
                ResetPressure();
                return;
            }
            if (!changed || !pressed) return;
            _recovering = false;
            AnimatePhase(true, arrival ?? TimeSpan.Zero);
            return;
        }
        active &= enabled && !_track;
        if (_active == active && enabled) return;
        _active = active;
        if (!enabled)
        {
            _motion.StopAnimation("Pulse");
            _motion.InsertScalar("Pulse", 0);
            return;
        }
        using var spring = _visual.Compositor.CreateSpringScalarAnimation();
        spring.FinalValue = active ? 1 : 0;
        spring.DampingRatio = 0.82f;
        spring.Period = TimeSpan.FromMilliseconds(active ? 85 : 125);
        _motion.StartAnimation("Pulse", spring);
    }

    public void ScheduleShrink(TimeSpan movement, bool animate)
    {
        if (!_floating || !_enabled || _active) return;
        if (!animate) { ResetPressure(); return; }
        _recovering = true;
        AnimatePhase(false, movement);
    }

    private void ResetPressure()
    {
        _recovering = false;
        _motion.StopAnimation("Phase");
        _motion.InsertVector4("Phase", FloatingTabPressure.Rest);
    }

    private void AnimatePhase(bool pressed, TimeSpan movement)
    {
        using var animation = _visual.Compositor.CreateVector4KeyFrameAnimation();
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.Duration = FloatingTabPressure.ClockDuration;
        using var linear = _visual.Compositor.CreateLinearEasingFunction();
        animation.InsertExpressionKeyFrame(0, FloatingTabPressure.PhaseExpression(pressed, movement, false), linear);
        animation.InsertExpressionKeyFrame(1, FloatingTabPressure.PhaseExpression(pressed, movement, true), linear);
        _motion.StartAnimation("Phase", animation);
    }

    public void BindGlass(LiquidGlassBrush? glass)
    {
        if (ReferenceEquals(_glass, glass)) return;
        _glass?.StopScalarExpression("RefThickness");
        _glass = glass;
        if (_floating && _deformation is not null)
        {
            _glass?.BindScalarExpression("RefThickness",
                "20+M.Pulse*8+Min(48,Max(Max(Abs(F.StrainX)*M.RestWidth,Abs(F.StrainY)*M.RestHeight),Abs(F.StrainXY)*Max(M.RestWidth,M.RestHeight))*0.22)",
                "M", _motion, "F", _deformation);
            return;
        }
        // Legacy surfaces derive optical stretch from their visual size.
        // The refraction band grows with that actual stretch, not pointer speed.
        _glass?.BindScalarExpression("RefThickness", _track
            ? "20+M.Pulse*2"
            : "20+M.Pulse*8+Min(48,Abs(V.Size.X-M.RestWidth)*0.22)",
            "M", _motion, "V", _visual);
    }

    public void Dispose()
    {
        if (_floating) _motion.StopAnimation("Phase");
        BindGlass(null);
        _visual.StopAnimation("Scale");
        _visual.StopAnimation("CenterPoint");
        _motion.StopAnimation("Pulse");
        _motion.Dispose();
    }
}
