using YogaITS.Tray.UI.Glass.Layout;
using System.Numerics;

namespace YogaITS.Tray.UI.Glass.Animation;

// Persistent world-space left/top/right/bottom positions and velocities.
// Input changes moving reference targets, never the integrated state. Velocity
// always stays in world coordinates through drag, release, retarget and impact.
// Only explicit reset and unilateral contact constraints can project position.
internal sealed class FourEdgePhysics
{
    private readonly bool _layoutOnly, _elasticLayout;
    private bool _layoutMotion, _elasticLayoutActive;
    public FourEdgePhysics(bool layoutOnly = false, bool elasticLayout = false)
    {
        _layoutOnly = _layoutMotion = layoutOnly; _elasticLayout=elasticLayout;
    }
    public const double FixedStep = 1.0 / 240;
    public const float ClickMotionRate = 2f / 3;
    // Removing volume coupling slowed neutral layout to roughly half the
    // capsule's inflation response. Match its ~125 ms 90% arrival instead.
    public const float LayoutMotionRate = ClickMotionRate * 2;
    private const float LayoutFrequency = 16;
    private const float LayoutDamping = 0.72f;
    private const float DragAccelerationTransfer = 0.18f;
    private const float DragVelocityTransfer = 0.72f;
    public Vector4 Position { get; private set; }
    public Vector4 Velocity { get; private set; }
    public Vector4 Acceleration { get; private set; }
    public Vector2 AppliedDragAcceleration { get; private set; }
    public TabFrame Target { get; private set; }
    public bool Dragging { get; private set; }
    private int _contacts;
    private int _blockedInputSides;
    private Vector2? _horizontalBounds, _verticalBounds;
    private bool _recoverHorizontal, _recoverVertical;
    private Vector4 _pressOutsets = new(12); // Default 6-DIP resting gap, doubled per edge.
    public bool FrontContact => _contacts != 0;
    public bool Pressed { get; private set; }
    private Vector2 _direction, _dragDirection, _pointerVelocity, _driverVelocity, _pointerSampleCenter;
    public Vector2 ReferenceVelocity => _pointerVelocity;
    public Vector2 EffectiveReferenceVelocity => LimitDragDrive(_pointerVelocity);
    private double _accumulator, _pointerAge, _pointerTime;
    private Vector2 Center => new((Position.X + Position.Z) / 2, (Position.Y + Position.W) / 2);
    private static Vector2 CenterOf(TabFrame frame) => new(frame.Center, frame.Y + frame.Height / 2);
    public TabFrame Frame => new(Position.X, Position.Y, Position.Z - Position.X, Position.W - Position.Y);
    private Vector4 Goal
    {
        get
        {
            var outsets = Pressed ? _pressOutsets : Vector4.Zero;
            var center = CenterOf(Target) + new Vector2((outsets.Z - outsets.X) / 2, (outsets.W - outsets.Y) / 2);
            float w = (Target.Width + outsets.X + outsets.Z) / 2;
            float h = (Target.Height + outsets.Y + outsets.W) / 2;
            if (_horizontalBounds is Vector2 bounds)
            {
                w = Math.Min(w, (bounds.Y - bounds.X) / 2);
                center.X = Math.Clamp(center.X, bounds.X + w, bounds.Y - w);
            }
            if (_verticalBounds is Vector2 vertical)
            {
                h = Math.Min(h, (vertical.Y - vertical.X) / 2);
                center.Y = Math.Clamp(center.Y, vertical.X + h, vertical.Y - h);
            }
            return new(center.X - w, center.Y - h, center.X + w, center.Y + h);
        }
    }
    public bool IsSettled => MaxAbs(Position - Goal) < 0.025f && MaxAbs(Velocity) < 0.2f &&
        _driverVelocity.LengthSquared() < 0.04f && (!Dragging || EffectiveReferenceVelocity.LengthSquared() < 0.04f);
    private static float MaxAbs(Vector4 v) => Math.Max(Math.Max(Math.Abs(v.X), Math.Abs(v.Y)), Math.Max(Math.Abs(v.Z), Math.Abs(v.W)));

    public void Reset(TabFrame frame)
    {
        _layoutMotion = _layoutOnly;
        _elasticLayoutActive=false;
        Target = frame;
        Position = Goal;
        Velocity = Acceleration = Vector4.Zero;
        AppliedDragAcceleration = Vector2.Zero;
        _direction = _dragDirection = _pointerVelocity = _driverVelocity = Vector2.Zero;
        _pointerSampleCenter = CenterOf(frame);
        _accumulator = _pointerAge = _pointerTime = 0;
        _contacts = _blockedInputSides = 0;
        Dragging = false;
        _recoverHorizontal = _recoverVertical = false;
        ConstrainTrack();
    }

    public void ConfigureTrack(TabFrame track, TabFrame first, TabFrame last, bool constrainVertical = false, bool preserveFrame = false)
    {
        var gaps = new Vector4(Math.Max(0, first.X - track.X), Math.Max(0, first.Y - track.Y),
            Math.Max(0, track.X + track.Width - last.X - last.Width),
            Math.Max(0, track.Y + track.Height - last.Y - last.Height));
        // Mirror each resting inset through its corresponding track edge.
        // Width uses the endpoint padding, never the entire four-option track.
        _pressOutsets = gaps * 2;
        Vector2? vertical = constrainVertical ? new Vector2(track.Y - gaps.Y, track.Y + track.Height + gaps.W) : null;
        if (_verticalBounds != vertical)
            _recoverVertical = preserveFrame && !Dragging && vertical is Vector2 v && (Position.Y < v.X || Position.W > v.Y);
        _verticalBounds = vertical;
        _blockedInputSides = 0;
        SetHorizontalBounds(track.X - gaps.X, track.X + track.Width + gaps.Z, preserveFrame);
        ConstrainTrack();
    }

    public void SetHorizontalBounds(float left, float right, bool preserveFrame = false)
    {
        if (!float.IsFinite(left) || !float.IsFinite(right) || right - left < 4)
            throw new ArgumentOutOfRangeException(nameof(right));
        var bounds = new Vector2(left, right);
        if (_horizontalBounds == bounds) return;
        _recoverHorizontal = preserveFrame && !Dragging && (Position.X < left || Position.Z > right);
        _horizontalBounds = bounds;
        _blockedInputSides = 0;
        ConstrainTrack();
    }

    public void SetPressed(bool pressed)
    {
        if (Pressed == pressed) return;
        // A completed translation must not bias a later stationary press.
        if (IsSettled && !Dragging) { _direction = Vector2.Zero; _elasticLayoutActive=false; }
        Pressed = pressed;
        _contacts = 0;
    }

    public void SetTarget(TabFrame frame, bool? layout = null)
    {
        bool nextLayout = layout ?? _layoutOnly;
        bool releasedDrag = Dragging;
        if (Dragging)
        {
            // Velocity is already world-space momentum. Do not add the pointer
            // velocity again or zero the common velocity when releasing.
            Dragging = false;
            _blockedInputSides = 0;
            _pointerVelocity = Vector2.Zero;
            _pointerAge = 0.06;
        }
        if (Target == frame && !releasedDrag) return;
        _layoutMotion=nextLayout;
        _elasticLayoutActive=_elasticLayout && _layoutMotion;
        var displacement = CenterOf(frame) - Center;
        if(_elasticLayoutActive)
        {
            // Only translation has a travel direction. A one-sided resize must
            // never kick its fixed edge; a stationary press stays centered.
            float left=frame.X-Target.X, right=frame.X+frame.Width-Target.X-Target.Width;
            float top=frame.Y-Target.Y, bottom=frame.Y+frame.Height-Target.Y-Target.Height;
            displacement=new(left*right>0.001f?(left+right)/2:0,top*bottom>0.001f?(top+bottom)/2:0);
        }
        var commonVelocity = new Vector2((Velocity.X + Velocity.Z) / 2, (Velocity.Y + Velocity.W) / 2);
        _direction = _layoutMotion && !_elasticLayoutActive ? Vector2.Zero : displacement.LengthSquared() > 0.01f ? Vector2.Normalize(displacement)
            : !_layoutMotion && commonVelocity.LengthSquared() > 1 ? Vector2.Normalize(commonVelocity) : Vector2.Zero;
        Target = frame;
        _contacts = 0;
    }

    public void DragTo(TabFrame frame, double now)
    {
        var center = CenterOf(frame);
        if (!Dragging)
        {
            _pointerVelocity = Vector2.Zero;
            _driverVelocity = new((Velocity.X + Velocity.Z) / 2, (Velocity.Y + Velocity.W) / 2);
            _pointerSampleCenter = center;
            _pointerTime = now;
        }
        else
        {
            double elapsed = now - _pointerTime;
            if (elapsed >= 0.0001)
            {
                var velocity = elapsed <= 0.12 ? (center - _pointerSampleCenter) / (float)elapsed : Vector2.Zero;
                float speed = velocity.Length();
                _pointerVelocity = speed > 3000 ? velocity * (3000 / speed) : velocity;
                if (_pointerVelocity.LengthSquared() > 1) _dragDirection = Vector2.Normalize(_pointerVelocity);
                _pointerSampleCenter = center;
                _pointerTime = now;
            }
        }
        _pointerAge = 0;
        Target = frame;
        _layoutMotion = _layoutOnly;
        Dragging = true;
        _contacts = 0;
        // The input reference follows the pointer immediately. Both physical
        // edges respond with finite acceleration, retaining common inertial lag.
    }

    public void StopPointer()
    {
        _pointerVelocity = Vector2.Zero;
        _pointerAge = 0.06;
    }

    public void Advance(double elapsed)
    {
        if (!double.IsFinite(elapsed) || elapsed <= 0) return;
        // Drop excess stalled time; never accumulate a backlog of simulation frames.
        _accumulator += Math.Min(elapsed, 0.1);
        while (_accumulator + 1e-9 >= FixedStep)
        {
            Integrate((float)FixedStep);
            _accumulator = Math.Max(0, _accumulator - FixedStep);
        }
    }

    private void Integrate(float dt)
    {
        var goal = Goal;
        float width = Math.Max(4, Position.Z - Position.X), height = Math.Max(4, Position.W - Position.Y);
        float restW = goal.Z - goal.X, restH = goal.W - goal.Y;
        float pressure = Math.Clamp(restW * restH / (width * height) - 1, -2, 2) * 260;
        float wallW = width > restW * 1.6f ? (restW * 1.6f - width) * 700 : width < restW * 0.6f ? (restW * 0.6f - width) * 700 : 0;
        float wallH = height > restH * 1.6f ? (restH * 1.6f - height) * 700 : height < restH * 0.6f ? (restH * 0.6f - height) * 700 : 0;
        float motionRate = Dragging ? 1 : _layoutMotion ? LayoutMotionRate : ClickMotionRate;
        // Time-scale all click/snap restoring forces and damping together,
        // preserving their elastic proportions without overriding velocity.
        // Layout reflow moves only the edges whose layout coordinates changed.
        // Fluid volume pressure would incorrectly push a fixed left edge outward.
        var coupling = _layoutMotion ? Vector4.Zero : new Vector4(-pressure * width / 2 - wallW, -pressure * height / 2 - wallH,
            pressure * width / 2 + wallW, pressure * height / 2 + wallH) * (motionRate * motionRate);
        _pointerAge += dt;
        if (_pointerAge > 0.05) _pointerVelocity = Vector2.Zero;
        UpdateDragWallDrive();
        var desiredVelocity = Dragging ? EffectiveReferenceVelocity : Vector2.Zero;
        var driverDelta = (desiredVelocity - _driverVelocity) * (1 - MathF.Exp(-45 * dt));
        _driverVelocity += driverDelta;
        AppliedDragAcceleration = Dragging ? LimitDragDrive(driverDelta / dt * DragAccelerationTransfer) : Vector2.Zero;
        var dragDirection = _driverVelocity.LengthSquared() > 1 ? Vector2.Normalize(_driverVelocity) : _dragDirection;
        var nextA = Vector4.Zero;
        for (int i = 0; i < 4; i++)
        {
            var normal = i switch { 0 => -Vector2.UnitX, 1 => -Vector2.UnitY, 2 => Vector2.UnitX, _ => Vector2.UnitY };
            float facing = _layoutMotion && !_elasticLayoutActive ? 0 : Vector2.Dot(normal, Dragging ? dragDirection : _direction);
            bool leading = !Dragging && facing > 0.5f;
            bool trailing = !Dragging && facing < -0.5f;
            // A faster front and a softer rear let acceleration visibly stretch
            // the body. On contact, rear momentum becomes compression instead
            // of being damped away before the front reaches the boundary.
            float omega = Dragging ? (facing > 0.5f ? 28 : facing < -0.5f ? 16 : 18)
                : _elasticLayoutActive ? leading ? 20 : trailing ? 14 : LayoutFrequency
                : leading ? 30 : trailing ? (FrontContact ? 14 : 18) : LayoutFrequency;
            omega *= motionRate;
            float damping = Dragging ? (EffectiveReferenceVelocity.LengthSquared() > 25 ? 1 : 0.72f)
                : _elasticLayoutActive ? leading ? 0.68f : 0.56f
                : leading ? 0.68f : trailing ? (FrontContact ? 0.46f : 0.76f) : LayoutDamping;
            float referenceSpeed = Dragging ? (i is 0 or 2 ? _driverVelocity.X : _driverVelocity.Y) * DragVelocityTransfer : 0;
            float appliedAcceleration = i is 0 or 2 ? AppliedDragAcceleration.X : AppliedDragAcceleration.Y;
            // Both edges receive the SAME signed input acceleration. Mixed
            // reference/world damping retains viscosity and steady-motion lag.
            // Relative edge deformation comes only from their elastic response.
            float a = omega * omega * (goal[i] - Position[i]) -
                2 * damping * omega * (Velocity[i] - referenceSpeed) + coupling[i] + appliedAcceleration;
            // A short soft-contact zone absorbs forward speed before the rigid
            // final boundary. Other edges and perpendicular expansion stay free.
            if (leading)
            {
                float sign = i is 0 or 1 ? -1 : 1;
                float gap = (goal[i] - Position[i]) * sign;
                float margin = Math.Max(4, (i is 0 or 2 ? restW : restH) * 0.04f);
                if (gap < margin && Velocity[i] * sign > 0)
                    a -= Velocity[i] * 4 * motionRate * MathF.Pow(Math.Clamp(1 - gap / margin, 0, 1), 2);
            }
            nextA[i] = a;
        }
        Acceleration = ApplyTrackReaction(nextA);
        var previousPosition = Position;
        Velocity += Acceleration * dt;
        Position += Velocity * dt;
        // Drag still has no centering or pointer-leading constraint. Only the
        // optional track boundaries constrain the actual hull on each enabled axis.
        if (!Dragging && !_layoutMotion)
        {
            for (int i = 0; i < 4; i++)
            {
                var normal = i switch { 0 => -Vector2.UnitX, 1 => -Vector2.UnitY, 2 => Vector2.UnitX, _ => Vector2.UnitY };
                if (Vector2.Dot(normal, _direction) <= 0.5f) continue;
                float sign = i is 0 or 1 ? -1 : 1;
                float gap = (goal[i] - Position[i]) * sign;
                float previousGap = (goal[i] - previousPosition[i]) * sign;
                // If a press-size change moved the boundary inward, preserve
                // the existing state and let forces return it. Only a genuine
                // inside-to-outside crossing is an impact against the new wall.
                bool crossed = previousGap >= 0 && gap <= 0;
                bool restingContact = gap >= 0 && gap < 0.15f && Math.Abs(Velocity[i]) < 20;
                if ((_contacts & (1 << i)) != 0 || crossed || restingContact)
                {
                    Position = With(Position, i, goal[i]);
                    Velocity = With(Velocity, i, 0);
                    _contacts |= 1 << i;
                }
            }
        }
        ConstrainTrack();
    }

    private void ConstrainTrack()
    {
        ConstrainAxis(_horizontalBounds, 0, 2);
        ConstrainAxis(_verticalBounds, 1, 3);
        ConstrainExtent(0, 2);
        ConstrainExtent(1, 3);
        UpdateDragWallDrive();
        Acceleration = ApplyTrackReaction(Acceleration);
    }

    private void ConstrainExtent(int near, int far)
    {
        const float minimum = 4;
        if (Position[far] - Position[near] >= minimum) return;
        // Opposing edges can collide after a large resize while carrying old
        // momentum. Resolve that physical contact without restarting the solver.
        bool nearContact = (_contacts & (1 << near)) != 0;
        bool farContact = (_contacts & (1 << far)) != 0;
        float center = nearContact ? Position[near] + minimum / 2 :
            farContact ? Position[far] - minimum / 2 : (Position[near] + Position[far]) / 2;
        Position = With(With(Position, near, center - minimum / 2), far, center + minimum / 2);
        if (Velocity[far] < Velocity[near])
        {
            float common = nearContact || farContact ? 0 : (Velocity[near] + Velocity[far]) / 2;
            Velocity = With(With(Velocity, near, common), far, common);
        }
    }

    private void ConstrainAxis(Vector2? limits, int near, int far)
    {
        if (limits is not Vector2 bounds) return;
        float start = Position[near], end = Position[far];
        bool recovering = near == 0 ? _recoverHorizontal : _recoverVertical;
        // A layout resize pulls an existing hull back with forces instead of
        // projecting it into smaller bounds. Captured drags still use hard walls.
        if (recovering && !Dragging && (start < bounds.X || end > bounds.Y)) return;
        if (near == 0) _recoverHorizontal = false; else _recoverVertical = false;
        if (end - start > bounds.Y - bounds.X) { start = bounds.X; end = bounds.Y; }
        else if (end < bounds.X) { end += bounds.X - start; start = bounds.X; }
        else if (start > bounds.Y) { start += bounds.Y - end; end = bounds.Y; }
        else { start = Math.Max(start, bounds.X); end = Math.Min(end, bounds.Y); }
        Position = With(With(Position, near, start), far, end);
        if (start <= bounds.X && Velocity[near] < 0) Velocity = With(Velocity, near, 0);
        if (end >= bounds.Y && Velocity[far] > 0) Velocity = With(Velocity, far, 0);
    }

    private Vector2 LimitDragDrive(Vector2 value)
    {
        if (((_blockedInputSides & 1) != 0 && value.X < 0) ||
            ((_blockedInputSides & 2) != 0 && value.X > 0)) value.X = 0;
        if (((_blockedInputSides & 4) != 0 && value.Y < 0) ||
            ((_blockedInputSides & 8) != 0 && value.Y > 0)) value.Y = 0;
        return value;
    }

    private void UpdateDragWallDrive()
    {
        if (!Dragging || _horizontalBounds is not Vector2 bounds)
        {
            _blockedInputSides = 0;
            return;
        }
        float rawLeft = Target.X - (Pressed ? _pressOutsets.X : 0);
        float rawRight = Target.X + Target.Width + (Pressed ? _pressOutsets.Z : 0);
        // Retain the block while the input remains beyond the endpoint. Merely
        // bouncing a fraction inside must not re-enable the outward motor.
        if (rawLeft > bounds.X + 0.01f) _blockedInputSides &= ~1;
        if (rawRight < bounds.Y - 0.01f) _blockedInputSides &= ~2;
        if (Position.X <= bounds.X && (rawLeft < bounds.X || _pointerVelocity.X < 0 || _driverVelocity.X < 0))
            _blockedInputSides |= 1;
        if (Position.Z >= bounds.Y && (rawRight > bounds.Y || _pointerVelocity.X > 0 || _driverVelocity.X > 0))
            _blockedInputSides |= 2;
        if (_verticalBounds is Vector2 vertical)
        {
            float rawTop = Target.Y - (Pressed ? _pressOutsets.Y : 0);
            float rawBottom = Target.Y + Target.Height + (Pressed ? _pressOutsets.W : 0);
            if (rawTop > vertical.X + 0.01f) _blockedInputSides &= ~4;
            if (rawBottom < vertical.Y - 0.01f) _blockedInputSides &= ~8;
            if (Position.Y <= vertical.X && (rawTop < vertical.X || _pointerVelocity.Y < 0 || _driverVelocity.Y < 0))
                _blockedInputSides |= 4;
            if (Position.W >= vertical.Y && (rawBottom > vertical.Y || _pointerVelocity.Y > 0 || _driverVelocity.Y > 0))
                _blockedInputSides |= 8;
        }
        else _blockedInputSides &= ~12;
        // This is reference/motor state, never the physical capsule momentum.
        _driverVelocity = LimitDragDrive(_driverVelocity);
        AppliedDragAcceleration = LimitDragDrive(AppliedDragAcceleration);
    }

    private Vector4 ApplyTrackReaction(Vector4 acceleration)
    {
        if (_horizontalBounds is not Vector2 bounds) return acceleration;
        if (Position.X <= bounds.X && Velocity.X <= 0 && acceleration.X < 0) acceleration.X = 0;
        if (Position.Z >= bounds.Y && Velocity.Z >= 0 && acceleration.Z > 0) acceleration.Z = 0;
        if (_verticalBounds is Vector2 vertical)
        {
            if (Position.Y <= vertical.X && Velocity.Y <= 0 && acceleration.Y < 0) acceleration.Y = 0;
            if (Position.W >= vertical.Y && Velocity.W >= 0 && acceleration.W > 0) acceleration.W = 0;
        }
        return acceleration;
    }
    private static Vector4 With(Vector4 value, int index, float next) { value[index] = next; return value; }
}
