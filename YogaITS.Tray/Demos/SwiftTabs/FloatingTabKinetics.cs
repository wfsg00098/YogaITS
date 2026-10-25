using System.Numerics;

namespace YogaITS.Tray.Demos.SwiftTabs;

// Symmetric 2D strain: cross terms preserve arbitrary velocity directions.
internal readonly record struct GlassStrain(float X, float Y, float XY)
{
    public bool IsZero => X == 0 && Y == 0 && XY == 0;
    public static float Limit(float value)
    {
        if (value > 0.30f)
        {
            float excess = value - 0.30f;
            return 0.30f + 0.06f * excess / (0.06f + excess);
        }
        if (value < -0.20f)
        {
            float excess = -value - 0.20f;
            return -0.20f - 0.04f * excess / (0.04f + excess);
        }
        return value;
    }

    public static string LimitExpression(string value)
    {
        string high = "Max(0," + value + "-0.30)";
        string low = "Max(0,-" + value + "-0.20)";
        return "(" + value + ">0.30 ? 0.30+0.06*" + high + "/(0.06+" + high + ") : (" +
            value + "< -0.20 ? -0.20-0.04*" + low + "/(0.04+" + low + ") : " + value + "))";
    }

    public Vector2 Apply(Vector2 value) => new((1 + Limit(X)) * value.X + Math.Clamp(XY, -0.24f, 0.24f) * value.Y,
        Math.Clamp(XY, -0.24f, 0.24f) * value.X + (1 + Limit(Y)) * value.Y);

    public Vector2 ForwardOffset(Vector2 size, Vector2 velocity,
        float remainingDistance = float.PositiveInfinity, Vector2? pressureScale = null) =>
        ResolveAnchor(size, velocity, remainingDistance, pressureScale).Offset;

    public (Vector2 Offset, bool FrontAnchored) ResolveAnchor(Vector2 size, Vector2 velocity,
        float remainingDistance, Vector2? pressureScale = null, bool frontAnchored = false, bool centerAnchored = false)
    {
        if (centerAnchored || velocity.LengthSquared() < 0.001f) return (Vector2.Zero, false);
        var direction = Vector2.Normalize(velocity);
        var normal = direction * (pressureScale ?? Vector2.One);
        float radius = Math.Min(size.X, size.Y) / 2;
        var core = size / 2 - new Vector2(radius);
        float Support(Vector2 value) => Vector2.Dot(core, Vector2.Abs(value)) + radius * value.Length();
        float extension = Support(Apply(normal)) - Support(normal);
        float remaining = Math.Max(0, remainingDistance);
        // Contact persists until a new movement: changing strain must not let
        // the front detach while the logical center is still approaching.
        frontAnchored |= remaining <= extension + Math.Abs(extension);
        float forward = frontAnchored ? remaining - extension : Math.Abs(extension);
        return (direction * (forward / Math.Max(0.001f, Vector2.Dot(direction, normal))), frontAnchored);
    }
}

internal readonly record struct VelocityChange(Vector2 Previous, Vector2 Current)
{
    public Vector2 Delta => Current - Previous;
    public bool Significant(Vector2 size) =>
        (Current == Vector2.Zero && Previous != Vector2.Zero) ||
        Delta.Length() >= Math.Max(30, Math.Max(size.X, size.Y) * 0.35);

    public GlassStrain Stretch(Vector2 size)
    {
        float speed = Current.Length();
        if (speed < 0.001f) return default;
        var direction = Current / speed;
        float length = Math.Clamp(speed / (Math.Max(1, Math.Max(size.X, size.Y)) * 25), 0, 0.26f);
        float compression = 0.5f * (1 - 1 / (1 + length));
        // +length along velocity, -compression along its perpendicular.
        return new((length + compression) * direction.X * direction.X - compression,
            (length + compression) * direction.Y * direction.Y - compression,
            (length + compression) * direction.X * direction.Y);
    }

    public GlassStrain Impulse(Vector2 size)
    {
        if (!Significant(size)) return default;
        var reference = Current.LengthSquared() > 0.001f ? Current : Previous;
        if (reference.LengthSquared() < 0.001f) return default;
        var direction = Vector2.Normalize(reference);
        var perpendicular = new Vector2(-direction.Y, direction.X);
        float w = Math.Max(1, size.X), h = Math.Max(1, size.Y);
        float extent = MathF.Sqrt(w * w * direction.X * direction.X + h * h * direction.Y * direction.Y);
        float longitudinal = Math.Clamp(Vector2.Dot(Delta, direction) / (extent * 18), -0.20f, 0.22f);
        // Couple the axes: shortening along motion pushes material sideways.
        // For a straight stop, (1+longitudinal)*(1+transverse) is exactly one.
        float transverse = -longitudinal / (1 + longitudinal);
        float shear = Math.Clamp(Vector2.Dot(Delta, perpendicular) / (extent * 36), -0.12f, 0.12f);
        return new(
            longitudinal * direction.X * direction.X + transverse * perpendicular.X * perpendicular.X +
                2 * shear * direction.X * perpendicular.X,
            longitudinal * direction.Y * direction.Y + transverse * perpendicular.Y * perpendicular.Y +
                2 * shear * direction.Y * perpendicular.Y,
            (longitudinal - transverse) * direction.X * direction.Y +
                shear * (direction.X * perpendicular.Y + perpendicular.X * direction.Y));
    }
}

// Monotonic samples with signed two-dimensional position and velocity.
internal sealed class FloatingTabKinetics
{
    private Vector2 _origin, _target, _velocity;
    private double _time;
    private bool _linear;
    public TimeSpan Duration { get; private set; }
    private double TravelSeconds => Duration.TotalSeconds;

    public void Reset(Vector2 center, double now)
    {
        _origin = _target = center;
        _time = now;
        _velocity = Vector2.Zero;
        _linear = false;
        Duration = TimeSpan.Zero;
    }

    public Vector2 PositionAt(double now) => _linear
        ? Vector2.Lerp(_origin, _target, (float)Math.Clamp((now - _time) / TravelSeconds, 0, 1)) : _target;

    public Vector2 VelocityAt(double now) => _linear
        ? (now - _time < TravelSeconds ? _velocity : Vector2.Zero)
        : (now - _time <= 0.12 ? _velocity : Vector2.Zero);

    public bool TryContinue(Vector2 target, double now, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_linear || Vector2.DistanceSquared(_target, target) > 0.0001f) return false;
        remaining = TimeSpan.FromSeconds(Math.Max(0, TravelSeconds - (now - _time)));
        return true;
    }

    public VelocityChange MoveTo(Vector2 center, double now, bool animate, TimeSpan? duration = null)
    {
        var previous = VelocityAt(now);
        var position = PositionAt(now);
        double elapsed = now - _time;
        Duration = animate ? duration ?? FloatingTabSpring.MovementDuration(center - position) : TimeSpan.Zero;
        bool moving = animate && Duration > TimeSpan.Zero;
        var velocity = animate ? moving ? (center - position) / (float)TravelSeconds : Vector2.Zero
            : elapsed > 0.0001 && elapsed <= 0.12 ? (center - position) / (float)elapsed : Vector2.Zero;
        _origin = moving ? position : center;
        _target = center;
        _time = now;
        _velocity = velocity;
        _linear = moving;
        return new(previous, velocity);
    }

    public VelocityChange Stop(double now)
    {
        var previous = _linear ? _velocity : VelocityAt(now);
        var position = PositionAt(now);
        Reset(position, now);
        return new(previous, Vector2.Zero);
    }
}
