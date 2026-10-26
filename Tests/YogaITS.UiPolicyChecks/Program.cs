using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Backgrounds;
using YogaITS.Tray.UI.Glass.Capture;
using YogaITS.Tray.UI.Glass.Layout;
using YogaITS.Tray.UI.Glass.Rendering;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Settings;
using YogaITS.Tray.UI.Glass.Windowing;
using System.Numerics;
using YogaITS.Tray.Services;

int checks = AccelerationChecks.Run() + TrackEndpointChecks.Run() + OptionBarChecks.Run() + MultiRowOptionChecks.Run() + ButtonPressChecks.Run() + DynamicOptionChecks.Run() + ReflowChecks.Run() + LayoutTimingChecks.Run() + ImageBackgroundChecks.Run() + CaptureRetirementChecks.Run() + ElasticLayoutChecks.Run() + MeasuredChoiceChecks.Run();
void Check(bool pass, string message)
{
    checks++;
    if (!pass) throw new InvalidOperationException(message);
}
var moving = new YogaITS.Tray.UI.Glass.Animation.SelectionMotionState();
int oldMotion = moving.Begin(true), latestMotion = moving.Begin(true);
Check(!moving.Complete(oldMotion) && moving.IsMoving, "Interrupted animation completion cannot end a newer optical stretch");
Check(moving.IsMoving, "Inflation remains active throughout the current motion");
Check(moving.Complete(latestMotion) && !moving.IsMoving, "Inflation releases only after the current animation completes");
Check(!moving.Complete(latestMotion), "Repeated completion cannot release motion twice");
foreach (var (leading, dragging, oldPeriod) in new[] { (true,false,265), (false,false,410), (true,true,105), (false,true,155) })
    Check(Math.Abs(YogaITS.Tray.UI.Glass.Animation.SelectionMotionState.Period(leading,dragging).TotalMilliseconds * 3 - oldPeriod) < 0.001,
        "Click and drag spring periods are exactly one third of the initial demo");

foreach (double width in new[] { 240.0, 352, 600, 960 })
for (int selected = 0; selected < 4; selected++)
{
    var reference = YogaITS.Tray.UI.Glass.Layout.ChoiceTrackLayout.Create(width, 80);
    for (int i=0; i<4; i++)
    {
        var frame = reference.Items[i];
        Check(frame.Width > 0 && frame.Height >= 48 && frame.X >= 0 && frame.X+frame.Width <= width+0.01,
            "Reference tab frames fit the host at all selections and widths");
        Check(reference.Hit(frame.Center) == i, "Reference pointer hit testing uses actual icon/expanded frames");
        Check(reference.Hit(frame.X+1) == i && reference.Hit(frame.X+frame.Width-1) == i,
            "Expanded item edges cannot be stolen by a nearby collapsed icon");
        if(i>0) Check(frame.X >= reference.Items[i-1].X+reference.Items[i-1].Width,
            "Reference items never overlap while computing settled geometry");
    }
    Check(reference.Hit(-500) == 0 && reference.Hit(100000) == 3, "Reference scrubbing clamps to the outer destinations");
}

// A single excursion returns to rest without another inverted lobe or ringing.
Check(YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Impulse(0) == 0 &&
    YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Impulse(1) == 0 &&
    YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Response(0) == 0 &&
    YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Response(1) == 1,
    "Inertia has exact rest and target endpoints");
bool hasPeak = false, descending = false, singleExcursion = true, monotone = true;
float lastImpulse = 0, lastResponse = 0;
for (int i = 0; i <= 100; i++)
{
    float impulse = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Impulse(i / 100.0);
    Check(float.IsFinite(impulse) && impulse >= 0 && impulse < 1.05, "A single bounded impulse never reverses direction");
    hasPeak |= impulse > 0.5;
    if (impulse < lastImpulse) descending = true;
    if (descending && impulse > lastImpulse + 0.00001) singleExcursion = false;
    float response = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Response(i / 100.0);
    monotone &= float.IsFinite(response) && response >= lastResponse && response <= 1;
    lastImpulse = impulse;
    lastResponse = response;
}
Check(hasPeak && singleExcursion && monotone, "Duang once, return to rest, and stop without a second bounce");
var kinetics = new YogaITS.Tray.UI.Glass.Animation.FloatingTabKinetics();
kinetics.Reset(Vector2.Zero, 0);
var acceleration = kinetics.MoveTo(new(10, 0), 0.01, false);
Check(acceleration.Impulse(new(100, 100)).X > 0, "Starting motion triggers stretch");
var steady = kinetics.MoveTo(new(20, 0), 0.02, false);
Check(steady.Impulse(new(100, 100)).IsZero, "Constant velocity does not retrigger a spring");
var braking = kinetics.MoveTo(new(25, 0), 0.03, false);
Check(braking.Impulse(new(100, 100)).X < 0, "Deceleration triggers compression independently of a target");
var reversal = kinetics.MoveTo(new(15, 0), 0.04, false);
Check(reversal.Impulse(new(100, 100)).X > 0, "Direction reversal produces a new impulse");
Check(kinetics.Stop(0.05).Impulse(new(100, 100)).X < 0 && kinetics.Stop(0.06).Impulse(new(100, 100)).IsZero,
    "Stopping recoils once without changing position");
kinetics.Reset(Vector2.Zero, 1);
kinetics.MoveTo(new(100, 0), 1, true);
Check(Vector2.Distance(kinetics.PositionAt(1 + kinetics.Duration.TotalSeconds / 2), new(50, 0)) < 0.001,
    "Linear travel remains uniform regardless of inertia");
Check(kinetics.Stop(1 + kinetics.Duration.TotalSeconds).Impulse(new(100, 100)).X < 0,
    "Animated arrival independently triggers a stopping impulse");

// Releasing at an existing target keeps the current trajectory and end time.
var uninterrupted = new YogaITS.Tray.UI.Glass.Animation.FloatingTabKinetics();
uninterrupted.Reset(Vector2.Zero, 0);
uninterrupted.MoveTo(new(224, 0), 0, true);
Check(uninterrupted.TryContinue(new(224, 0), 0.05, out var remainingTravel) &&
    Math.Abs(remainingTravel.TotalMilliseconds - 174) < 0.001,
    "Same-target release uses remaining time rather than starting a full movement");
Check(Vector2.Distance(uninterrupted.PositionAt(0.15), new(150, 0)) < 0.001 &&
    Vector2.Distance(uninterrupted.VelocityAt(0.15), new(1000, 0)) < 0.001,
    "Continuing a click does not alter position or velocity");
Check(!uninterrupted.TryContinue(new(230, 0), 0.05, out _),
    "A genuinely different snap target still requires retargeting");
Check(uninterrupted.TryContinue(new(224, 0), 0.25, out remainingTravel) && remainingTravel == TimeSpan.Zero,
    "A pending completion notification cannot create an extra movement after arrival");

Check(Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.MinimumTravelSpeed - 1000 * 2.0 / 3) < 0.001 &&
    Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.MovementDuration(new(20, 0)).TotalMilliseconds - 30) < 0.001,
    "Minimum speed is two thirds of its previous value and a 20 DIP short move takes 30 ms");

// Short moves use a minimum speed rather than a fixed 224 ms wait. The
// kinematics, continuation deadline and scale alignment share the same duration.
foreach (float distance in new[] { 0f, 0.25f, 0.99f, 1f, 2f, 8f, 20f, 90f, 224f, 1000f })
foreach (var direction in new[] { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, new Vector2(0.6f, 0.8f) })
{
    var destination = direction * distance;
    var duration = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.MovementDuration(destination);
    Check(duration >= TimeSpan.Zero && duration <= YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.TravelDuration,
        "Distance-aware travel never exceeds the existing maximum duration");
    var shortMove = new YogaITS.Tray.UI.Glass.Animation.FloatingTabKinetics();
    shortMove.Reset(Vector2.Zero, 0);
    var change = shortMove.MoveTo(destination, 0, true);
    Check(shortMove.Duration == duration, "The position model uses the actual planned travel duration");
    if (duration == TimeSpan.Zero)
    {
        Check(Vector2.Distance(shortMove.PositionAt(0), destination) < 0.0001 && shortMove.VelocityAt(0) == Vector2.Zero,
            "Sub-millisecond moves snap without dividing by zero or inventing velocity");
        Check(!shortMove.TryContinue(destination, 0, out _), "An immediate move has no delayed arrival to wait for");
        continue;
    }
    Check(change.Current.Length() >= YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.MinimumTravelSpeed - 0.2,
        "Actual vector speed reaches the minimum at every direction and distance");
    Check(Vector2.Distance(shortMove.PositionAt(duration.TotalSeconds / 2), destination / 2) < 0.001,
        "Short-move position remains linear and agrees with native keyframe timing");
    Check(shortMove.TryContinue(destination, duration.TotalSeconds / 4, out var rest) &&
        Math.Abs(rest.TotalSeconds - duration.TotalSeconds * 0.75) < 0.000001,
        "Same-target release preserves the shortened trajectory and remaining time");
    Check(Vector2.Distance(shortMove.PositionAt(duration.TotalSeconds), destination) < 0.001 &&
        shortMove.VelocityAt(duration.TotalSeconds) == Vector2.Zero,
        "Arrival and stopping velocity use the shortened deadline");
    var growthDelay = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.GrowthDelay(duration);
    var growth = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.GrowDuration;
    Check(duration >= growth ? growthDelay + growth == duration : growthDelay == TimeSpan.Zero,
        "Growth end-aligns with longer travel and starts immediately with shorter travel without shortening");
}

// Equal speeds with different directions must still produce inertia.
var turn = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(new(500, 0), new(0, 500));
Check(turn.Delta == new Vector2(-500, 500) && turn.Significant(new(100, 100)) && !turn.Impulse(new(100, 100)).IsZero,
    "Velocity keeps two-dimensional direction, not just scalar speed");
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(0, 800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var direction = Vector2.Normalize(velocity);
    var perpendicular = new Vector2(-direction.Y, direction.X);
    var strain = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, velocity).Stretch(new(150, 48));
    var along = strain.Apply(direction);
    var across = strain.Apply(perpendicular);
    Check(Vector2.Dot(along, direction) > 1 && Vector2.Dot(across, perpendicular) < 1,
        "Glass stretches along velocity and compresses its orthogonal direction");
    Check(Math.Abs(along.X * direction.Y - along.Y * direction.X) < 0.00001,
        "Oblique stretch retains the full vector direction");
}
// Sample actual capsule outlines independently of the analytical offset.
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(-800, 0), new Vector2(0, 800), new Vector2(0, -800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var size = new Vector2(150, 48);
    var direction = Vector2.Normalize(velocity);
    var change = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, velocity);
    foreach (var strain in new[] { change.Stretch(size), change.Impulse(size) })
    {
        var offset = strain.ForwardOffset(size, velocity);
        float radius = Math.Min(size.X, size.Y) / 2;
        var core = size / 2 - new Vector2(radius);
        float beforeMin = float.PositiveInfinity, beforeMax = float.NegativeInfinity;
        float afterMin = float.PositiveInfinity, afterMax = float.NegativeInfinity;
        for (int degree = 0; degree < 360; degree++)
        {
            float angle = degree * MathF.PI / 180;
            var normal = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var point = new Vector2(MathF.CopySign(core.X, normal.X), MathF.CopySign(core.Y, normal.Y)) + radius * normal;
            float before = Vector2.Dot(point, direction);
            float after = Vector2.Dot(strain.Apply(point) + offset, direction);
            beforeMin = Math.Min(beforeMin, before); beforeMax = Math.Max(beforeMax, before);
            afterMin = Math.Min(afterMin, after); afterMax = Math.Max(afterMax, after);
        }
        Check(afterMin >= beforeMin - 0.01 && afterMax > beforeMax,
            "Stretch adds length only in the positive velocity direction");
        Check(Math.Abs(afterMin - beforeMin) < 0.01,
            "The trailing edge follows base motion instead of extending backward");
    }
}

// A water-like stop shortens the travel axis while bulging across it.
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(-800, 0), new Vector2(0, 800), new Vector2(0, -800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var direction = Vector2.Normalize(velocity);
    var perpendicular = new Vector2(-direction.Y, direction.X);
    var recoil = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(velocity, Vector2.Zero).Impulse(new(150, 48));
    float along = Vector2.Dot(recoil.Apply(direction), direction);
    float across = Vector2.Dot(recoil.Apply(perpendicular), perpendicular);
    Check(along < 1 && across > 1,
        "Braking compresses the motion direction and expands the perpendicular direction");
    Check(Math.Abs(along * across - 1) < 0.00001,
        "Coupled stop deformation preserves area in its principal directions");
}

// Sample the stop contour: the front meets a wall, the rear squeezes forward,
// and both perpendicular sides bulge before returning to the original shape.
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(-800, 0), new Vector2(0, 800), new Vector2(0, -800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var size = new Vector2(150, 48);
    var direction = Vector2.Normalize(velocity);
    var perpendicular = new Vector2(-direction.Y, direction.X);
    var recoil = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(velocity, Vector2.Zero).Impulse(size);
    foreach (float weight in new[] { 0f, 0.25f, 0.5f, 1f, 0.5f, 0.25f, 0f })
    {
        var strain = new YogaITS.Tray.UI.Glass.Animation.GlassStrain(recoil.X * weight, recoil.Y * weight, recoil.XY * weight);
        var offset = strain.ForwardOffset(size, velocity);
        float radius = Math.Min(size.X, size.Y) / 2;
        var core = size / 2 - new Vector2(radius);
        var beforeMin = new Vector2(float.PositiveInfinity);
        var beforeMax = new Vector2(float.NegativeInfinity);
        var afterMin = beforeMin;
        var afterMax = beforeMax;
        for (int degree = 0; degree < 360; degree++)
        {
            float angle = degree * MathF.PI / 180;
            var normal = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var point = new Vector2(MathF.CopySign(core.X, normal.X), MathF.CopySign(core.Y, normal.Y)) + radius * normal;
            var deformed = strain.Apply(point) + offset;
            var before = new Vector2(Vector2.Dot(point, direction), Vector2.Dot(point, perpendicular));
            var after = new Vector2(Vector2.Dot(deformed, direction), Vector2.Dot(deformed, perpendicular));
            beforeMin = Vector2.Min(beforeMin, before); beforeMax = Vector2.Max(beforeMax, before);
            afterMin = Vector2.Min(afterMin, after); afterMax = Vector2.Max(afterMax, after);
        }
        Check(Math.Abs(afterMax.X - beforeMax.X) < 0.01,
            "Stopping compression fixes the forward contour instead of retreating from the wall");
        if (weight > 0)
        {
            Check(afterMin.X > beforeMin.X,
                "Only the rear contour squeezes toward the fixed leading edge");
            Check(afterMin.Y < beforeMin.Y && afterMax.Y > beforeMax.Y && Math.Abs(afterMin.Y + afterMax.Y) < 0.01,
                "Both perpendicular contours bulge symmetrically during the stopping recoil");
        }
        else Check(Vector2.Distance(beforeMin, afterMin) < 0.01 && Vector2.Distance(beforeMax, afterMax) < 0.01,
            "A completed stop restores the entire original contour without residual offset");
    }
}

// The logical center arriving is not enough: moving stretch can still carry
// the visible front past the destination. Sample the full pressure-scaled hull.
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(-800, 0), new Vector2(0, 800), new Vector2(0, -800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var direction = Vector2.Normalize(velocity);
    var size = new Vector2(150, 48);
    var start = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, velocity);
    var stop = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(velocity, Vector2.Zero);
    foreach (var pressure in new[] { Vector2.One, new Vector2(1.1f, 1.42f) })
    foreach (var strain in new[] { start.Stretch(size), start.Impulse(size), stop.Impulse(size), default(YogaITS.Tray.UI.Glass.Animation.GlassStrain) })
    {
        float lastFront = float.NegativeInfinity;
        for (int step = 0; step <= 100; step++)
        {
            float remaining = 300 * (1 - step / 100f);
            var offset = strain.ForwardOffset(size, velocity, remaining, pressure);
            var unconstrained = strain.ForwardOffset(size, velocity, pressureScale: pressure);
            float radius = Math.Min(size.X, size.Y) / 2;
            var core = size / 2 - new Vector2(radius);
            float front = float.NegativeInfinity, wall = float.NegativeInfinity;
            for (int degree = 0; degree < 360; degree++)
            {
                float angle = degree * MathF.PI / 180;
                var normal = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var point = new Vector2(MathF.CopySign(core.X, normal.X), MathF.CopySign(core.Y, normal.Y)) + radius * normal;
                var actual = -direction * remaining + (strain.Apply(point) + offset) * pressure;
                front = Math.Max(front, Vector2.Dot(actual, direction));
                wall = Math.Max(wall, Vector2.Dot(point * pressure, direction));
            }
            Check(front <= wall + 0.01,
                "The pressure-scaled leading contour never overshoots the destination wall");
            Check(front >= lastFront - 0.01,
                "Approaching a wall cannot make the leading edge retreat or jump backward");
            if (Vector2.DistanceSquared(offset, unconstrained) > 0.00001f || step == 100)
                Check(Math.Abs(front - wall) < 0.01,
                    "Residual extension stays behind the fixed front through arrival and recoil");
            lastFront = front;
        }
    }
}

// Once the stretched front touches the wall, a later reduction of stretch
// cannot switch back to a rear/center anchor before logical arrival.
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(-800, 0), new Vector2(0, 800), new Vector2(0, -800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var size = new Vector2(150, 48);
    var direction = Vector2.Normalize(velocity);
    var perpendicular = new Vector2(-direction.Y, direction.X);
    var start = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, velocity).Stretch(size);
    var stop = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(velocity, Vector2.Zero).Impulse(size);
    foreach (var pressure in new[] { Vector2.One, new Vector2(1.1f, 1.42f) })
    {
        bool frontAnchored = false;
        bool sawContactBeforeArrival = false;
        foreach (var (remaining, strain) in new[]
        {
            (300f, start), (20f, start), (1f, start),
            (0.8f, default(YogaITS.Tray.UI.Glass.Animation.GlassStrain)),
            (0.6f, stop), (0f, stop),
            (0f, default(YogaITS.Tray.UI.Glass.Animation.GlassStrain))
        })
        {
            bool wasAnchored = frontAnchored;
            var resolved = strain.ResolveAnchor(size, velocity, remaining, pressure, frontAnchored);
            frontAnchored = resolved.FrontAnchored;
            float radius = Math.Min(size.X, size.Y) / 2;
            var core = size / 2 - new Vector2(radius);
            float actualFront = float.NegativeInfinity, wall = float.NegativeInfinity;
            float actualRear = float.PositiveInfinity, logicalRear = float.PositiveInfinity;
            float acrossMin = float.PositiveInfinity, acrossMax = float.NegativeInfinity;
            float originalAcrossMin = float.PositiveInfinity, originalAcrossMax = float.NegativeInfinity;
            for (int degree = 0; degree < 360; degree++)
            {
                float angle = degree * MathF.PI / 180;
                var normal = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var point = new Vector2(MathF.CopySign(core.X, normal.X), MathF.CopySign(core.Y, normal.Y)) + radius * normal;
                var actual = -direction * remaining + (strain.Apply(point) + resolved.Offset) * pressure;
                actualFront = Math.Max(actualFront, Vector2.Dot(actual, direction));
                actualRear = Math.Min(actualRear, Vector2.Dot(actual, direction));
                logicalRear = Math.Min(logicalRear, Vector2.Dot(-direction * remaining + point * pressure, direction));
                wall = Math.Max(wall, Vector2.Dot(point * pressure, direction));
                // Remove only the longitudinal anchor translation: transverse
                // strain must remain untouched by the contact constraint.
                var deformationOnly = strain.Apply(point) * pressure;
                acrossMin = Math.Min(acrossMin, Vector2.Dot(deformationOnly, perpendicular));
                acrossMax = Math.Max(acrossMax, Vector2.Dot(deformationOnly, perpendicular));
                originalAcrossMin = Math.Min(originalAcrossMin, Vector2.Dot(point * pressure, perpendicular));
                originalAcrossMax = Math.Max(originalAcrossMax, Vector2.Dot(point * pressure, perpendicular));
            }
            Check(!wasAnchored || frontAnchored, "Contact cannot revert to a rear anchor within the same movement");
            if (frontAnchored)
            {
                sawContactBeforeArrival |= remaining > 0;
                Check(Math.Abs(actualFront - wall) < 0.01,
                    "The front stays at the wall while extension disappears and rear compression begins");
            }
            else Check(Math.Abs(actualRear - logicalRear) < 0.01,
                "Before contact the rear is fixed and extension travels only forward");
            if (strain == stop)
                Check(acrossMin < originalAcrossMin && acrossMax > originalAcrossMax,
                    "The perpendicular sides remain free to bulge after front contact");
        }
        Check(sawContactBeforeArrival, "Anchor switching follows contour contact, not logical-center arrival");
        var next = start.ResolveAnchor(size, velocity, 300, pressure);
        Check(!next.FrontAnchored, "A genuinely new movement starts again from the rear anchor");
    }
}

// Drag inertia is symmetric around the pointer-driven center, including a
// pause or reversal. It must bypass both the wall and edge-anchor compensation.
foreach (var velocity in new[] { new Vector2(800, 0), new Vector2(-800, 0), new Vector2(0, 800), new Vector2(0, -800), new Vector2(600, 800), new Vector2(-600, 800) })
{
    var size = new Vector2(150, 48);
    var start = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, velocity);
    var stop = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(velocity, Vector2.Zero);
    var reverse = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(velocity, -velocity);
    foreach (var strain in new[] { start.Stretch(size), start.Impulse(size), stop.Impulse(size), reverse.Impulse(size) })
    foreach (var pressure in new[] { Vector2.One, new Vector2(1.1f, 1.42f) })
    {
        var anchor = strain.ResolveAnchor(size, velocity, 0, pressure, frontAnchored: true, centerAnchored: true);
        Check(anchor.Offset == Vector2.Zero && !anchor.FrontAnchored,
            "Centered drag ignores destination contact and does not translate its strained hull");
        for (int degree = 0; degree < 360; degree += 15)
        {
            float angle = degree * MathF.PI / 180;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float radius = Math.Min(size.X, size.Y) / 2;
            var core = size / 2 - new Vector2(radius);
            var point = new Vector2(MathF.CopySign(core.X, direction.X), MathF.CopySign(core.Y, direction.Y)) + radius * direction;
            var positive = (strain.Apply(point) + anchor.Offset) * pressure;
            var negative = (strain.Apply(-point) + anchor.Offset) * pressure;
            Check((positive + negative).LengthSquared() < 0.00001f,
                "Drag acceleration, braking and reversal retain a symmetric center at every contour pair");
        }
    }
}

var slowShape = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, new(100, 0)).Stretch(new(150, 48));
var fastShape = new YogaITS.Tray.UI.Glass.Animation.VelocityChange(Vector2.Zero, new(700, 0)).Stretch(new(150, 48));
Check(fastShape.X > slowShape.X && fastShape.Y < slowShape.Y,
    "Greater speed increases directional length and orthogonal compression");

Check(YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.GrowthDelay(
    YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.TravelDuration) +
    YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.GrowDuration == YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.TravelDuration,
    "Independent growth duration aligns only its endpoint to movement arrival");
Check(YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.GrowthDelay(TimeSpan.Zero) == TimeSpan.Zero,
    "A capsule already under the pointer grows without a synthetic movement wait");

Check(new YogaITS.Tray.UI.Glass.Animation.VelocityChange(new(1, 0), Vector2.Zero).Significant(new(150, 48)) &&
    new YogaITS.Tray.UI.Glass.Animation.VelocityChange(new(1, 0), Vector2.Zero).Stretch(new(150, 48)).IsZero,
    "Stopping at very low speed still restores the undeformed shape");

foreach (var (movement, effect) in new[] { (224, 90), (90, 90), (40, 90), (0, 90), (224, 500) })
{
    var delay = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.EndAlignedDelay(
        TimeSpan.FromMilliseconds(movement), TimeSpan.FromMilliseconds(effect));
    Check(effect <= movement ? delay.TotalMilliseconds + effect == movement : delay == TimeSpan.Zero,
        "Shrink aligns its end when it fits, otherwise starts with movement without shortening");
}

// Saturation must stay bounded without the long constant-width plateau of a
// hard clamp when velocity stretch and spring impulse overlap.
float priorLimit = float.NegativeInfinity;
for (int i = -1000; i <= 1000; i++)
{
    float value = i / 1000f;
    float bounded = YogaITS.Tray.UI.Glass.Animation.GlassStrain.Limit(value);
    Check(bounded > -0.24 && bounded < 0.36 && bounded > priorLimit,
        "Soft strain saturation remains strictly increasing within the original safety bounds");
    if (value >= -0.20f && value <= 0.30f)
        Check(bounded == value, "Normal strains retain their original amplitude");
    priorLimit = bounded;
}
float highDuringHandoff = float.NegativeInfinity, lowDuringHandoff = float.PositiveInfinity;
for (int milliseconds = 106; milliseconds <= 224; milliseconds++)
{
    double t = milliseconds / 420.0;
    float raw = 0.26f * YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Response(t) +
        0.22f * YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.Impulse(t);
    float rendered = YogaITS.Tray.UI.Glass.Animation.GlassStrain.Limit(raw);
    highDuringHandoff = Math.Max(highDuringHandoff, rendered);
    lowDuringHandoff = Math.Min(lowDuringHandoff, rendered);
}
Check(highDuringHandoff - lowDuringHandoff > 0.005,
    "The overlap that previously stayed clamped for 118 ms now retains visible shape variation");

// Pressure uses a single compositor phase; release plans a recovery segment
// without completing growth on the UI thread or changing its native clock.
foreach (int milliseconds in new[] { 0, 8, 20, 90, 224 })
{
    var movement = TimeSpan.FromMilliseconds(milliseconds);
    var held = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Begin(
        YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Rest, true, movement);
    float growEnd = held.W + (float)YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.GrowDuration.TotalSeconds;
    foreach (float at in new[] { 0f, 0.001f, growEnd / 2, growEnd - 0.001f, growEnd, growEnd + 0.5f })
    {
        var current = held; current.X = at;
        float before = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(current);
        var remaining = TimeSpan.FromSeconds(Math.Max(0, movement.TotalSeconds - at));
        var release = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Begin(current, false, remaining);
        Check(Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(release) - before) < 0.00001,
            "Releasing keeps the pressure value and native growth clock continuous");
        Check(release.X == at && release.W == held.W && release.Y >= growEnd,
            "Recovery cannot begin before the full delayed 90 ms growth completes");
        var maximum = release; maximum.X = release.Y;
        Check(Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(maximum) - 1) < 0.00001,
            "A scheduled recovery reaches full inflation with no callback gap");
        var halfway = release; halfway.X = release.Y + 0.045f;
        var completed = release; completed.X = release.Y + 0.09f;
        Check(Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(halfway) - 0.5f) < 0.00001 &&
            Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(completed)) < 0.00001,
            "Recovery keeps its full independent 90 ms span with balanced easing");
        var repeated = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Begin(halfway, false, movement);
        Check(repeated.Y == halfway.Y && repeated.X == halfway.X &&
            Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(repeated) - 0.5f) < 0.00001,
            "Repeated layout/selection updates do not restart a shrinking capsule");
        var repress = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Begin(halfway, true, movement);
        Check(Math.Abs(YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Pulse(repress) - 0.5f) < 0.00001,
            "Pressing during recovery captures actual pressure instead of jumping to full size");
    }
    var early = held; early.X = growEnd / 2;
    var earlyRelease = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Begin(early, false, TimeSpan.Zero);
    var earlyRepress = YogaITS.Tray.UI.Glass.Animation.FloatingTabPressure.Begin(earlyRelease, true, TimeSpan.Zero);
    Check(earlyRepress.X == early.X && earlyRepress.W == early.W && earlyRepress.Z == early.Z && earlyRepress.Y < 0,
        "An early release/repress preserves the existing complete growth segment");
}
foreach (int milliseconds in new[] { 1, 8, 20, 90, 224 })
{
    var stop = TimeSpan.FromMilliseconds(milliseconds);
    var epsilon = TimeSpan.FromSeconds(0.0001);
    var left = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaWeights(stop - epsilon, stop);
    var center = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaWeights(stop, stop);
    var right = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaWeights(stop + epsilon, stop);
    var leftVelocity = (center - left) / (float)epsilon.TotalSeconds;
    var rightVelocity = (right - center) / (float)epsilon.TotalSeconds;
    Check(Vector4.Distance(leftVelocity, rightVelocity) < 0.3,
        "Planned stopping preserves strain velocity instead of pausing at a new zero-velocity spring");
    var settled = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaWeights(
        stop + YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaDuration, stop);
    Check(settled == Vector4.Zero, "The planned stop has an exact finite unstrained endpoint");
    for (int sample = 0; sample <= 100; sample++)
    {
        var elapsed = TimeSpan.FromTicks((stop + YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaDuration).Ticks * sample / 100);
        var weights = YogaITS.Tray.UI.Glass.Animation.FloatingTabSpring.InertiaWeights(elapsed, stop);
        Check(float.IsFinite(weights.X + weights.Y + weights.Z + weights.W),
            "Short-move handoffs have finite inertia coefficients at every phase");
    }
}

// Floating previews never change confirmed mode before a matching release.
foreach (double width in new[] { 352.0, 600, 960 })
for (int original = 0; original < 4; original++)
{
    var layout = YogaITS.Tray.UI.Glass.Layout.ChoiceTrackLayout.Create(width, 80);
    var capsule = layout.Capsule(original);
    int target = (original + 2) % 4;
    var destination = layout.Capsule(target);
    Check(capsule == layout.Items[original] &&
        Math.Abs(capsule.Center - layout.Items[original].Center) < 0.001,
        "Restored full-width glass shares its text and hit-target frame");
    var preview = new YogaITS.Tray.UI.Glass.Animation.FloatingTabGesture();
    double pressed = capsule.X + capsule.Width * 0.23;
    double released = destination.Center;
    Check(preview.Begin(10, pressed, original, layout), "A Floating session accepts the first pointer");
    Check(Math.Abs(preview.PreviewFrame.Center - Math.Clamp(pressed, layout.Capsule(0).Center, layout.Capsule(3).Center)) < 0.001,
        "Pointer-down previews the pressed position before any drag or activation");
    Check(!preview.Begin(11, pressed, target, layout), "Another finger cannot steal the held capsule");
    Check(preview.ConfirmedIndex == original && !preview.IsDragging, "Pressing inflates without activating a new mode");
    Check(!preview.Move(11, released, out _), "Other pointer motion cannot change the visual preview");
    Check(!preview.Move(10, pressed + 1, out _), "Press jitter does not start a drag");
    Check(preview.Move(10, released, out var visual) && preview.IsDragging,
        "The owning pointer can move the glass preview");
    Check(preview.ConfirmedIndex == original && preview.PreviewIndex == target,
        "Crossing other modes keeps confirmed selection unchanged until release");
    Check(Math.Abs(visual.X - destination.X) < 0.001,
        "The capsule center follows the held pointer directly");
    Check(!preview.Release(11, released, out _) && preview.ConfirmedIndex == original,
        "Another pointer release cannot activate the preview");
    Check(preview.Release(10, released, out int committed) && committed == target && preview.ConfirmedIndex == target,
        "The owning release commits the final preview exactly once");
    Check(!preview.Release(10, released, out _), "Duplicate release cannot activate a mode twice");
    Check(preview.Begin(20, pressed, original, layout), "A subsequent gesture can start normally");
    preview.Move(20, released, out _);
    preview.Cancel();
    Check(preview.ConfirmedIndex == original && !preview.Release(20, released, out _),
        "Cancellation preserves the original mode and never commits a drag");
    Check(preview.Begin(30, layout.Items[target].Center, original, layout) && preview.ConfirmedIndex == original &&
        Math.Abs(preview.PreviewFrame.Center - layout.Items[target].Center) < 0.001,
        "Pressing another label moves the preview there without activating it");
    Check(preview.Release(30, layout.Items[target].Center, out committed) && committed == target,
        "A click activates its label on release");
    preview.Begin(40, pressed, original, layout);
    Check(preview.Move(40, -10000, out visual) && visual.X == layout.Capsule(0).X,
        "Captured preview motion clamps safely at the left destination");
    Check(preview.Move(40, 100000, out visual) && Math.Abs(visual.X-layout.Capsule(3).X) < 0.001,
        "Captured preview motion clamps safely at the right destination");
    preview.Cancel();
}

#if LIQUID_GLASS
Check(AppIdentity.TrayProcessName == "YogaITS.Tray", "glass application process name");
Check(AppIdentity.AppUserModelId == "YogaITS", "glass application identity");
#else
Check(AppIdentity.TrayProcessName == "YogaITS.Tray", "native application process name");
Check(AppIdentity.AppUserModelId == "YogaITS", "native application identity");
#endif

var presentation = new GlassPresentationPreference();
Check(presentation.NativeRequested && !presentation.UseGlass(true), "Even a capable machine starts in native UI");
Check(!presentation.SetNativeRequested(true), "Reapplying native preference does not cause a visual transition");
Check(presentation.SetNativeRequested(false) && presentation.UseGlass(true), "Explicit selection enables glass only when available");
Check(!presentation.UseGlass(false), "Loss of glass capability immediately uses native presentation");
Check(presentation.UseGlass(true), "A temporary system transparency restriction does not overwrite the selected preference");
Check(presentation.SetNativeRequested(true) && !presentation.UseGlass(true), "Switching back stops using glass while capability remains available");

// The glass client fills its HWND without introducing a legacy window region.
NativeFrameChecks.CheckWindow(Check);
foreach (var (hit, cursor) in new (nint, int)[] { (10, 32644), (11, 32644), (12, 32645), (15, 32645),
    (13, 32642), (17, 32642), (14, 32643), (16, 32643), (0, 0), (1, 0), (2, 0), (8, 0), (9, 0), (20, 0) })
    Check(GlassEdgePolicy.ResizeCursor(hit) == cursor, "Resize cursors cover four edges/corners without replacing content or caption-button cursors");
foreach (nint native in new nint[] { 0, 1, 2, 10, 11, 12, 13, 14, 15, 16, 17, 18 })
{
    Check(GlassEdgePolicy.ResizeHitTest(0, 599, 480, 600, 4, native) == 16,
        "WinUI content and border hits resolve the bottom-left resize corner consistently");
    Check(GlassEdgePolicy.ResizeHitTest(479, 0, 480, 600, 4, native) == 14,
        "WinUI content and border hits resolve the top-right resize corner consistently");
}
foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 })
{
    int width = (int)(480*scale), height = (int)(815*scale), grip = (int)Math.Round(4*scale);
    foreach (var (x, y, expected) in new[] {
        (0, 0, 13), (width-1, 0, 14), (0, height-1, 16), (width-1, height-1, 17),
        (width/2, 0, 12), (width/2, height-1, 15), (0, height/2, 10), (width-1, height/2, 11),
        (grip, grip, 1), (width/2, grip, 1), (-1, 0, 1), (width, 0, 1), (0, height, 1) })
        Check(GlassEdgePolicy.ResizeHitTest(x, y, width, height, grip, 1) == expected,
            "Inside resize grips follow DPI without consuming content or points outside the window");
    foreach (nint captionButton in new nint[] { 8, 9, 20 })
        Check(GlassEdgePolicy.ResizeHitTest(width-1, 0, width, height, grip, captionButton) == captionButton,
            "Native minimize/maximize/close hit results remain unchanged");
}

// Window-layering regressions: selecting only upper windows, transient missing
// targets, own-window feedback, and stable configuration when upper apps reorder.
CaptureWindowEntry[] order = [new(40), new(30), new(20), new(10), new(5)];
Check(CaptureExclusionPolicy.TryCreate(20, [20], order, out var above), "Known target anchors the exclusion list");
Check(above.SequenceEqual(new nint[] {20, 30, 40}), "Lower apps stay live while the target and upper apps are excluded");
Check(CaptureExclusionPolicy.TryCreate(30, [20, 30], order, out var second), "Second preview gets its own anchor");
Check(second.SequenceEqual(new nint[] {20, 30, 40}), "All own preview surfaces stay excluded to avoid mutual feedback");
Check(CaptureExclusionPolicy.TryCreate(20, [20, 30], order, out var first), "First preview also excludes the other preview");
CaptureWindowEntry[] twoWindows = [new(50), new(30), new(25), new(20), new(10)];
Check(CaptureExclusionPolicy.TryCreate(30, [20, 30], twoWindows, out second), "Upper preview computes a distinct capture scene");
Check(CaptureExclusionPolicy.TryCreate(20, [20, 30], twoWindows, out first), "Lower preview computes a distinct capture scene");
Check(!second.Contains(25) && first.Contains(25), "Foreign app between two previews is visible only behind the upper preview");
Check(CaptureExclusionPolicy.TryCreate(20, [20], [new(30), new(40), new(20), new(10)], out var reordered), "Reordered upper apps retain a valid anchor");
Check(above.SequenceEqual(reordered), "Swapping already-excluded apps does not restart or reconfigure capture");
Check(CaptureExclusionPolicy.TryCreate(20, [0, 20, 20], [new(0), new(30, Visible: false), new(40, Minimized: true), new(50, Cloaked: true), new(20)], out var filtered), "Invisible upper windows do not prevent capture");
Check(filtered.SequenceEqual(new nint[] {20}), "Hidden, minimized, cloaked and null HWNDs do not pollute the list");
Check(!CaptureExclusionPolicy.TryCreate(99, [99], order, out var missing) && missing.Length == 0, "A destroyed/missing anchor never excludes every enumerated app");
Check(!CaptureExclusionPolicy.TryCreate(0, [], order, out missing) && missing.Length == 0, "A null anchor cannot start a capture scene");
Check(CaptureExclusionPolicy.TryCreate(20, [], [new(30), new(20, Visible: false)], out filtered) && filtered.SequenceEqual(new nint[] {20, 30}), "Target visibility is independent from its Z-order anchor");
Check(CaptureExclusionPolicy.TryCreate(20, [20, 10], [new(40), new(40), new(20)], out filtered) && filtered.SequenceEqual(new nint[] {10, 20, 40}), "Repeated HWNDs are normalized and own lower windows never recurse");
Check(CaptureExclusionPolicy.IsWindowEvent(0x8004, -4, 0), "Desktop-client reorder wakes capture even without activation");
Check(!CaptureExclusionPolicy.IsWindowEvent(0x800B, 0, 0), "Moving a top-level app does not repeatedly recompute its unchanged Z order");
Check(!CaptureExclusionPolicy.IsWindowEvent(0x800B, -4, 0), "Client-content layout does not flood desktop-order updates");
Check(!CaptureExclusionPolicy.IsWindowEvent(0x800C, 0, 0), "Text changes do not reconfigure window exclusions");
Check(!CaptureExclusionPolicy.IsWindowEvent(0x8002, 0, 1), "Child object events do not trigger a desktop enumeration");
Check(CaptureExclusionPolicy.IsWindowEvent(3, 0, 0), "Foreign foreground changes wake inactive glass");
Check(CaptureExclusionPolicy.IsWindowEvent(0x16, 0, 0) && CaptureExclusionPolicy.IsWindowEvent(0x17, 0, 0), "Minimize and restore both refresh scene order");

// Cached topology must scale with actual changes, not capture/drag frequency.
var cache = new CaptureExclusionCache();
Check(!cache.TryRead(1, 100, out _), "Initial capture needs a real desktop-order snapshot");
cache.Store(1, 100, above);
for (int sample = 100; sample < 1100; sample++)
    Check(cache.TryRead(1, sample, out var cached) && ReferenceEquals(cached, above), "High-rate frame/drag notifications reuse the same allocation-free topology snapshot");
Check(!cache.TryRead(2, 101, out _), "A real topology event invalidates immediately without waiting for expiry");
Check(!cache.TryRead(1, 1100, out _), "A lazy one-second fallback catches missed window events");
Check(cache.Contains(40) && !cache.Contains(10), "Destroy filtering retains excluded windows but ignores unrelated lower HWNDs");
cache.Store(2, 1100, reordered);
Check(cache.TryRead(2, 1100, out _) && !cache.TryRead(1, 1100, out _), "New scene revision replaces the previous cached generation");
Check(!cache.TryRead(2, 1099, out _), "Invalid backwards timestamps cannot keep a stale cache alive");
Check(CaptureExclusionPolicy.IsWindowEvent(0x000B, 0, 0), "Ending a native move triggers one final topology recheck");
var rateMeter = new CaptureFrameRateMeter();
Check(rateMeter.Sample(10, 100, 80, 60) == default, "Initial counters are a baseline rather than lifetime FPS");
Check(rateMeter.Sample(12, 340, 200, 240) == new CaptureFrameRates(120, 60, 90), "Diagnostics separate capture, copied and drawn rates using actual elapsed time");
Check(rateMeter.Sample(12, 340, 200, 240) == new CaptureFrameRates(120, 60, 90), "Repeated diagnostics reads do not divide by zero or discard rates");
Check(rateMeter.Sample(14, 10, 10, 250) == default, "A replaced capture source resets rate baselines without negative FPS");
Check(rateMeter.Sample(double.NaN, 999, 999, 999) == default, "Invalid diagnostic timestamps do not corrupt the next sample");
Check(rateMeter.Sample(14.5, 70, 40, 280) == new CaptureFrameRates(120, 60, 60), "Diagnostics do not assume one-second timer accuracy");

// A blocked renderer must not block producers, replay obsolete geometry, run
// concurrently, or dispose resources while an in-flight draw is using them.
using (var entered = new ManualResetEventSlim())
using (var resume = new ManualResetEventSlim())
{
    var seen = new List<string>();
    int cleaned = 0, failuresReported = 0;
    var work = new LatestWorkLoop<string>(value =>
    {
        lock (seen) seen.Add(value);
        if (value == "first") { entered.Set(); if (!resume.Wait(5000)) throw new TimeoutException(); }
    }, _ => Interlocked.Increment(ref failuresReported), cleanup: () => Interlocked.Increment(ref cleaned));
    try
    {
        Check(work.Post("first") && entered.Wait(5000), "Drawing worker starts without any XAML or capture dependency");
        for (int i = 0; i < 1000; i++) Check(work.Post("obsolete-" + i), "UI geometry publication never waits for an in-flight draw");
        Check(work.Post("last"), "Newest geometry replaces all obsolete pending requests");
        resume.Set();
        Check(SpinWait.SpinUntil(() => { lock (seen) return seen.Contains("last"); }, 5000), "Worker eventually draws the newest request after producer pressure");
        work.Dispose();
        Check(work.Completion.Wait(5000), "Worker stops and cleans up after drawing");
        lock (seen) Check(seen.SequenceEqual(new[] {"first", "last"}), "No intermediate drag requests replay or execute concurrently");
        Check(cleaned == 1 && failuresReported == 0, "Worker resources are cleaned exactly once without errors");
        Check(!work.Post("after-close"), "A disposed worker rejects late capture callbacks");
    }
    finally { resume.Set(); work.Dispose(); work.Completion.Wait(5000); }
}
using (var entered = new ManualResetEventSlim())
using (var resume = new ManualResetEventSlim())
{
    int cleaned = 0, executed = 0;
    var work = new LatestWorkLoop<string>(_ => { Interlocked.Increment(ref executed); entered.Set(); if (!resume.Wait(5000)) throw new TimeoutException(); }, _ => { }, cleanup: () => Interlocked.Increment(ref cleaned));
    try
    {
        work.Post("active"); Check(entered.Wait(5000), "Close-during-draw scenario reached active work");
        work.Post("discard"); work.Dispose();
        Check(!work.Completion.IsCompleted && cleaned == 0, "Closing never waits for or prematurely disposes the active render resources");
        resume.Set(); Check(work.Completion.Wait(5000), "Deferred release finishes after active drawing returns");
        Check(executed == 1 && cleaned == 1, "Closing drops pending work and releases resources exactly once");
    }
    finally { resume.Set(); work.Dispose(); work.Completion.Wait(5000); }
}
int workerErrors = 0, workerCleanup = 0;
var brokenWorker = new LatestWorkLoop<string>(_ => throw new IOException("synthetic draw failure"), _ => Interlocked.Increment(ref workerErrors), cleanup: () => Interlocked.Increment(ref workerCleanup));
brokenWorker.Post("fail");
Check(brokenWorker.Completion.Wait(5000) && workerErrors == 1 && workerCleanup == 1, "Draw failure is reported once and still cleans up without killing the process");
Check(!brokenWorker.Post("retry"), "A failed drawing worker cannot restart into a disposed device");

// Authorization/lifecycle updates must remain data-only until the same worker
// finishes its active draw; dropping a pending draw cannot lose the preference.
using (var entered = new ManualResetEventSlim())
using (var resume = new ManualResetEventSlim())
{
    int uiThread = Environment.CurrentManagedThreadId, requestedBorder = 1, appliedBorder = 1;
    int ownerThread = 0, setterThread = 0, stopped = 0;
    var work = new LatestWorkLoop<string>(value =>
    {
        int current = Environment.CurrentManagedThreadId;
        if (value == "initial") { ownerThread = current; entered.Set(); if (!resume.Wait(5000)) throw new TimeoutException(); }
        else if (value == "apply") { setterThread = current; appliedBorder = Volatile.Read(ref requestedBorder); }
        else if (value == "pause") Interlocked.Increment(ref stopped);
    }, _ => { });
    try
    {
        work.Post("initial"); Check(entered.Wait(5000), "Authorization regression has an active render before consent completes");
        Volatile.Write(ref requestedBorder, 0); work.Post("obsolete"); work.Post("apply");
        Check(appliedBorder == 1 && setterThread == 0, "UI authorization publishes preference without executing a live session setter or waiting");
        resume.Set();
        Check(SpinWait.SpinUntil(() => Volatile.Read(ref appliedBorder) == 0, 5000), "Latest authorization state survives replacement of pending geometry work");
        Check(ownerThread == setterThread && setterThread != uiThread, "Capture creation and authorization setter share the drawing owner thread");
        work.Post("pause"); Check(SpinWait.SpinUntil(() => Volatile.Read(ref stopped) == 1, 5000), "Pause is applied by worker even without another capture frame");
    }
    finally { resume.Set(); work.Dispose(); Check(work.Completion.Wait(5000), "Authorization regression worker cleans up without a UI wait cycle"); }
}

// Identity orchestration tests never register a package or launch a process.
int registrations = 0, replacements = 0, failures = 0;
bool Register() { registrations++; return true; }
void Restart() { replacements++; }
void Failed(Exception _) { failures++; }
Check(CaptureIdentityFlow.ContinueStartup(true, false, Register, Restart, Failed), "Matching identity opens the UI without registration or restart");
Check(registrations == 0 && replacements == 0, "Correct-directory startup has no registration side effects");
Check(!CaptureIdentityFlow.ContinueStartup(false, false, Register, Restart, Failed), "Successful first registration hands off instead of opening duplicate windows");
Check(registrations == 1 && replacements == 1, "A repaired identity starts exactly one replacement");
Check(CaptureIdentityFlow.ContinueStartup(false, true, Register, Restart, Failed), "Missing identity after replacement still opens the UI");
Check(registrations == 1 && replacements == 1, "A failed identity activation cannot loop through registration/restart");
Check(CaptureIdentityFlow.ContinueStartup(false, false, () => false, Restart, Failed), "Denied/failed registration keeps the current app running");
Check(replacements == 1, "Failed registration never launches a replacement");
Check(CaptureIdentityFlow.ContinueStartup(false, false, () => throw new IOException("missing backend"), Restart, Failed), "Registration exceptions do not prevent startup");
Check(failures == 1 && replacements == 1, "Backend exception is reported once with no restart");
Check(CaptureIdentityFlow.ContinueStartup(false, false, Register, () => throw new IOException("restart failed"), Failed), "Failed replacement creation keeps the current app running");
Check(failures == 2, "Replacement failure is reported");
Check(CaptureIdentityFlow.SameDirectory(@"D:\LG UI", @"d:\lg ui\"), "Identity directories compare without case/trailing-separator mistakes");
Check(CaptureIdentityFlow.SameDirectory(@"D:\LG UI\..\LG UI", @"D:\LG UI\"), "Equivalent dot-segment paths do not cause repair loops");
Check(!CaptureIdentityFlow.SameDirectory(@"D:\LG UI", @"D:\LG UI moved"), "Moving the app invalidates the old external location");
Check(!CaptureIdentityFlow.SameDirectory(null, @"D:\LG UI"), "Missing restart marker never suppresses initial repair");
Check(!CaptureIdentityFlow.SameDirectory("", @"D:\LG UI"), "Empty identity directory never matches current location");
foreach (string[] arguments in new[]
{
    Array.Empty<string>(), new[] { "" }, new[] { "--native-preview", "two words", @"D:\LG UI\" },
    new[] { "quote\"inside", "\\\"", "line\nbreak", "$(never execute)", "中文路径" }
})
{
    var parsed = NativeArguments.Parse("app.exe " + CaptureIdentityFlow.CommandLine(arguments));
    Check(parsed.Skip(1).SequenceEqual(arguments), "Identity helper preserves exact Windows argv including quotes, spaces and trailing slashes");
}
Check(Math.Abs(GlassScalePolicy.HighlightRange(1) - 30) < 1e-9, "Preserve provisional 100% baseline");
Check(Math.Abs(GlassScalePolicy.HighlightRange(2) - 15) < 1e-9, "Revised 200% highlight baseline is 15");
double previous = double.PositiveInfinity;
for (int percent = 25; percent <= 800; percent++)
{
    double scale = percent / 100.0;
    double range = GlassScalePolicy.HighlightRange(scale);
    Check(double.IsFinite(range) && range is > 0 and <= 100, "Every supported scale has a valid range");
    Check(range <= previous, "Larger scale never broadens the material parameter");
    previous = range;
    double expectedWidth = 900 - 450 * Math.Clamp(scale - 1, 0, 1);
    Check(Math.Abs(range * range * scale - expectedWidth) < 1e-6, "Interpolate physical falloff width, not raw parameter");
    Check(GlassScalePolicy.BlurAmount(scale, true) == 0, "Captured background is already Gaussian-filtered; glass must not blur it again");
    Check(GlassScalePolicy.BlurAmount(scale, false) == 0, "Tiles do not blur the window background again");
}
foreach (double scale in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
{
    Check(GlassScalePolicy.HighlightRange(scale) == 30, "Invalid DPI safely falls back to 100%");
    Check(GlassScalePolicy.BlurAmount(scale, true) == 0, "Invalid DPI never enables a second blur pass");
    Check(GlassScalePolicy.BlurAmount(scale, false) == 0, "Tiles never enable blur even with invalid DPI");
}
foreach (bool dark in new[] { false, true })
{
    GlassTint window = GlassMaterialPolicy.Tint(dark, true, default);
    Check(window == new GlassTint(dark ? 0 : 255, dark ? 0 : 255, dark ? 0 : 255, 0.40), "Window is black 40% / white 40% intrinsic tint");
    GlassTint unselected = GlassMaterialPolicy.Tint(dark, false, default);
    Check(unselected.Amount == 0.06, "Unselected glass retains subtle base tint");
    GlassTint accent = GlassMaterialPolicy.Tint(dark, false, new(200, 90, 30, 0.35));
    Check(accent.Amount > unselected.Amount && accent.Amount <= 1, "Input highlights color the material, not a flat overlay");
    Check(accent.Red > accent.Green && accent.Green > accent.Blue, "Accent hue reaches all glass tint channels");
    GlassTint opaque = GlassMaterialPolicy.Tint(dark, false, new(200, 90, 30, 1));
    Check(opaque == new GlassTint(200, 90, 30, 1), "Opaque state tint overrides neutral material tint");
    GlassTint hover = GlassMaterialPolicy.Tint(dark, false, new(dark ? 255 : 0, dark ? 255 : 0, dark ? 255 : 0, 0.10));
    Check(hover.Red == hover.Green && hover.Green == hover.Blue, "Hover stays neutral in both themes");
    Check(GlassMaterialPolicy.Tint(dark, false, default) == unselected, "Clearing hover restores neutral glass without selection tint");
    foreach (double amount in new[] { -1.0, double.NaN, double.PositiveInfinity })
        Check(GlassMaterialPolicy.Tint(dark, true, new(0, 0, 0, amount)) == window, "Invalid tint amount cannot poison the shader");
}
foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2, 2.25, 3, 4 })
{
    Check(GlassEdgePolicy.ShapeInset * scale == 0, "Window and card edges never expose a DPI-scaled untinted gutter");
    foreach (var (side, radius) in new[] { (28.0, GlassEdgePolicy.CardRadius(28)), (48.0, GlassEdgePolicy.CardRadius(48)),
        (72.0, GlassEdgePolicy.CardRadius(72)), (820.0, GlassEdgePolicy.WindowRadiusDip) })
    {
        double physicalHalfSide = side * scale / 2 - GlassEdgePolicy.ShapeInset * scale;
        Check(Math.Abs(GlassEdgePolicy.ShapeRadius(side, radius) * physicalHalfSide - radius * scale) < 1e-6,
            "Window and capsule SDF radii remain correct at every DPI");
    }
}
Check(GlassEdgePolicy.HasReservedTopRow(900, 1400, 0, 1, 900, 1400), "Known WinUI top-row gap is detected");
Check(GlassEdgePolicy.HasReservedTopRow(900, 1400, 0, 1, 900, 1401), "A full-height site shifted down one pixel also covers the reserved row");
foreach (var (width, height, left, top, right, bottom) in new[]
{
    (900, 1400, 0, 0, 900, 1400), (900, 1400, 0, 2, 900, 1400),
    (900, 1400, 8, 1, 900, 1400), (900, 1400, 0, 1, 899, 1400),
    (900, 1400, 0, 1, 900, 1399), (0, 1400, 0, 1, 0, 1400), (900, 1, 0, 1, 900, 1)
})
    Check(!GlassEdgePolicy.HasReservedTopRow(width, height, left, top, right, bottom), "Other layouts and collapsed hosts are never repositioned");
var tuning = BackgroundTuning.Default;
Check(tuning == new BackgroundTuning(40, 40, 1), "Capture preview defaults: light 40%, dark 40%, Gaussian 1 DIP");
foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    Check(tuning.WithTint(invalid, false) == tuning && tuning.WithTint(invalid, true) == tuning, "Empty/invalid tint preserves both themes");
    Check(tuning.WithBlur(invalid) == tuning, "Empty/invalid blur preserves last valid value");
}
Check(tuning.WithTint(-1, false).TintPercent(false) == 0 && tuning.WithTint(101, true).TintPercent(true) == 100, "Tint calibration is bounded");
Check(tuning.WithTint(75, true).LightTintPercent == 40 && tuning.WithTint(75, false).DarkTintPercent == 40, "Theme calibration never overwrites the other theme");
Check(tuning.WithBlur(-1).BlurDip == 0 && tuning.WithBlur(101).BlurDip == 100, "Blur calibration is bounded");
foreach (double value in new[] { 0.0, 12.5, 50, 100 })
{
    var calibrated = tuning.WithTint(value, false).WithTint(value, true).WithBlur(value);
    foreach (bool dark in new[] { false, true })
    {
        var tint = GlassMaterialPolicy.Tint(dark, true, default, calibrated.TintAmount(dark));
        Check(tint.Amount == value / 100 && double.IsFinite(tint.Red), "Zero/decimal/maximum tint reaches material without NaN");
        Check(GlassMaterialPolicy.Tint(dark, false, default, calibrated.TintAmount(dark)) == GlassMaterialPolicy.Tint(dark, false, default),
            "Background tuning never changes card tint parameters");
    }
    foreach (double scale in new[] { 1.0, 1.25, 2, 4, 8 })
    {
        Check(calibrated.PhysicalBlur(scale) == Math.Min(250, value * scale), "Extra blur follows DPI and respects effect limits");

    }
}
Check(HdrToneMappingPolicy.WhiteScale(3) == 3, "SDR white at 240 nits is normalized against scRGB's 80-nit reference");
foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, 101 })
    Check(HdrToneMappingPolicy.WhiteScale(invalid) == 1, "Unavailable white metadata falls back to the explicit 80-nit reference");
double lastTone = 0;
for (int index = 0; index <= 10000; index++)
{
    double input = index / 100.0;
    double mapped = HdrToneMappingPolicy.Shoulder(input);
    Check(double.IsFinite(mapped) && mapped is >= 0 and <= 1, "HDR shoulder always fits the SDR target range");
    Check(mapped >= lastTone, "HDR highlights never invert or become darker as input brightness rises");
    if (input <= HdrToneMappingPolicy.Knee) Check(mapped == input, "SDR midtones below the shoulder are unchanged");
    lastTone = mapped;
}
Check(Math.Abs(HdrToneMappingPolicy.Shoulder(0.75 + 1e-8) - 0.75) < 1.1e-8, "HDR shoulder is continuous at the knee");
Check(HdrToneMappingPolicy.Shoulder(4) > HdrToneMappingPolicy.Shoulder(1), "HDR highlights roll off instead of all clipping to white");
Check(Math.Abs(HdrToneMappingPolicy.EncodeSrgb(0.21404114048223255) - 0.5) < 1e-9, "Linear scRGB mid-gray is encoded to sRGB exactly once");
foreach (double scale in new[] { 1.0, 2, 3, 6 })
{
    var neutral = HdrToneMappingPolicy.Map(0.2 * scale, 0.2 * scale, 0.2 * scale, scale);
    Check(Math.Abs(neutral.Red - HdrToneMappingPolicy.EncodeSrgb(0.2)) < 1e-9 && neutral.Red == neutral.Green && neutral.Green == neutral.Blue,
        "Changing Windows SDR white level does not brighten or tint normalized SDR content");
    var color = HdrToneMappingPolicy.Map(4 * scale, 2 * scale, scale, scale);
    Check(color.Red > color.Green && color.Green > color.Blue && color.Red <= 1, "Highlight rolloff preserves color ordering");
}
foreach (var sample in new[] { (-1.0, 0.0, 0.0), (65504.0, 12.0, 2.0), (double.NaN, double.PositiveInfinity, double.NegativeInfinity) })
{
    var mapped = HdrToneMappingPolicy.Map(sample.Item1, sample.Item2, sample.Item3, 3);
    Check(double.IsFinite(mapped.Red) && double.IsFinite(mapped.Green) && double.IsFinite(mapped.Blue), "Conversion handles negative/outlying channels without NaN output");
    Check(mapped.Red is >= 0 and <= 1 && mapped.Green is >= 0 and <= 1 && mapped.Blue is >= 0 and <= 1, "Mapped color always fits BGRA8");
}
foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2, 2.5, 3, 4 })
{
    var previousCapacity = new CaptureSurfaceSize(1, 1);
    for (int width = 300; width <= 1300; width++)
    {
        var viewport = DesktopCaptureViewport.Create(width, 777, scale);
        Check(viewport.CanDraw && viewport.PixelWidth == width && viewport.PixelHeight == 777, "Native viewport follows the current client rect, not stale XAML layout");
        Check(Math.Abs(viewport.WidthDip * scale - width) < 1e-9 && Math.Abs(viewport.HeightDip * scale - 777) < 1e-9,
            "Physical capture pixels retain 1:1 mapping at every supported DPI");
        var capacity = viewport.Capacity(previousCapacity, true, 16384);
        Check(capacity.Width >= width && capacity.Height >= 777 && capacity.Width % 128 == 0 && capacity.Height % 128 == 0,
            "Local drawing capacity covers the viewport and grows in 128-pixel buckets");
        Check(capacity.Width >= previousCapacity.Width && capacity.Height >= previousCapacity.Height, "Live resize never repeatedly shrinks/reallocates the drawing surface");
        previousCapacity = capacity;
    }
    var small = DesktopCaptureViewport.Create(300, 200, scale);
    Check(small.Capacity(previousCapacity, true, 16384) == previousCapacity, "Dragging inward reuses local capacity without stretching source pixels");
    Check(small.Capacity(previousCapacity, false, 16384) == new CaptureSurfaceSize(384, 256), "Ending resize trims unused capacity once");
    Check(DesktopCaptureViewport.Create(16380, 16380, scale).Capacity(default, false, 16384) == new CaptureSurfaceSize(16384, 16384), "Rounding never exceeds the GPU texture limit");
}
foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
    Check(DesktopCaptureViewport.Create(640, 480, invalid).Scale == 1, "Invalid transient DPI cannot corrupt capture geometry");
Check(!DesktopCaptureViewport.Create(0, 0, 2).CanDraw && !DesktopCaptureViewport.Create(-1, 100, 2).CanDraw,
    "Minimized/collapsed native viewports are not rendered or allocated as full windows");
var firstViewport = DesktopCaptureViewport.Create(958, 1627, 2);
var firstCapacity = firstViewport.Capacity(default, false, 16384);
Check(firstCapacity == new CaptureSurfaceSize(1024, 1664), "Reproduce first V4 capacity larger than visible viewport");
Check(CaptureSurfaceUpdates.Region(firstCapacity, firstViewport, false) == firstCapacity,
    "First BeginDraw covers the entire non-virtual surface, not just the viewport");
Check(CaptureSurfaceUpdates.Region(firstCapacity, firstViewport, true) == new CaptureSurfaceSize(958, 1627),
    "Partial updates begin only after complete initialization");
foreach (int width in new[] { 300, 700, 1025, 600, 1200, 300 })
{
    var viewport = DesktopCaptureViewport.Create(width, 1001, 1.5);
    var capacity = viewport.Capacity(firstCapacity, true, 16384);
    var update = CaptureSurfaceUpdates.Region(capacity, viewport, false);
    Check(update == capacity, "Every resized surface is fully reinitialized before partial drawing");
    var subsequent = CaptureSurfaceUpdates.Region(capacity, viewport, true);
    Check(subsequent.Width <= capacity.Width && subsequent.Height <= capacity.Height,
        "Every partial drawing region remains within actual allocated storage");
}
using (var slot = new LatestFrameSlot<TestFrame>())
{
    var frames = Enumerable.Range(0, 1000).Select(index => new TestFrame(index)).ToArray();
    foreach (var frame in frames) slot.Publish(frame);
    Check(frames.Take(999).All(frame => frame.DisposeCount == 1), "Every superseded frame is immediately released, never queued");
    Check(frames[^1].DisposeCount == 0, "A busy UI retains the final latest frame, not an older snapshot");
    using (var newest = slot.Take())
    {
        Check(newest == frames[^1] && newest.Index == 999, "The consumer jumps directly to the newest frame");
        Check(slot.Take() is null, "No old frames remain to replay after a slow consumer");
    }
    slot.Dispose();
    Check(frames.All(frame => frame.DisposeCount == 1), "Consumed frames are not double-disposed by slot cleanup");
    var final = new TestFrame(1000);
    slot.Publish(final); slot.Dispose();
    Check(final.DisposeCount == 1 && slot.Take() is null, "Stopping releases a pending final frame");
}

foreach(double sigma in new[]{0.0,1,2,12,250})
foreach(var point in new[]{(-200.0,-100.0),(0.0,0.0),(500.0,200.0),(3800.0,2100.0),(5000.0,3000.0)})
{
    var region=CaptureCopyRegion.Create(3840,2160,point.Item1,point.Item2,984,1640,sigma);
    Check(region.X>=0 && region.Y>=0 && region.Width>0 && region.Height>0 &&
        region.X+region.Width<=3840 && region.Y+region.Height<=2160,"Partial processing copy stays within the unchanged full-monitor capture");
}
Check(CaptureCopyRegion.Create(3840,2160,500,200,984,1640,2)==new CaptureCopyRegion(490,190,1024,1664),
    "Processing copy includes full four-sigma/interpolation neighbourhood without copying the rest of the monitor");

Console.WriteLine($"PASS: {checks} UI DPI/capture/HDR/identity checks; no hardware, capture session or user settings accessed.");

sealed class TestFrame(int index) : IDisposable
{
    public int Index { get; } = index;
    public int DisposeCount { get; private set; }
    public void Dispose() => DisposeCount++;
}

static class NativeArguments
{
    public static string[] Parse(string commandLine)
    {
        nint data = CommandLineToArgvW(commandLine, out int count);
        if (data == 0) throw new InvalidOperationException("Cannot parse test argv.");
        try
        {
            return Enumerable.Range(0, count).Select(index =>
                System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(data, index * IntPtr.Size))!).ToArray();
        }
        finally { _ = LocalFree(data); }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}

internal static class NativeFrameChecks
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectI { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointI { public int X, Y; }
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RectI rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RectI rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref PointI point);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    internal static void CheckWindow(Action<bool, string> check)
    {
        nint hwnd = CreateWindowExW(0, "STATIC", "Hidden glass frame check", 0x00CF0000, 0, 0, 480, 600, 0, 0, 0, 0);
        check(hwnd != 0, "Headless HWND is created without WS_VISIBLE");
        nint region = CreateRectRgn(0, 0, 0, 0);
        var frame = new GlassWindowFrame();
        int sizingRequests = 0;
        nuint sizingHit = 0;
        SubclassProc callback = (window, message, wParam, lParam, id, data) =>
        {
            if (message is 0x00A1 or 0x00A3) { sizingRequests++; sizingHit = wParam; return 0; }
            return frame.DefWindowProc(window, message, wParam, lParam);
        };
        check(SetWindowSubclass(hwnd, callback, 1, 0), "Headless HWND uses the actual production frame handler");
        nint child = CreateWindowExW(0, "STATIC", "Hidden full-client content site", 0x40000000, 0, 0, 480, 600, hwnd, 0, 0, 0);
        check(child != 0, "Hidden child reproduces the full-client input route without displaying a window");
        SubclassProc contentCallback = (window, message, wParam, lParam, id, data) =>
            frame.TryHandleContentMessage(hwnd, message, wParam, lParam, out nint handled) ? handled :
                DefSubclassProc(window, message, wParam, lParam);
        check(SetWindowSubclass(child, contentCallback, 1, 0), "Content child uses the production non-client input handler");
        try
        {
            check(GetClientRect(hwnd, out var nativeClient), "Read original client size");
            var nativeOrigin = new PointI();
            check(ClientToScreen(hwnd, ref nativeOrigin), "Read original client origin");
            foreach (var (x, y, width, height) in new[] { (0, 0, 480, 600), (17, 23, 500, 620), (-400, -200, 600, 500) })
            {
                frame.SetEnabled(hwnd, true);
                check(SetWindowPos(hwnd, 0, x, y, width, height, 0x14), "Move/resize the headless glass HWND");
                check(GetClientRect(hwnd, out var client) && GetWindowRect(hwnd, out var bounds), "Read real window/client bounds");
                GetWindowRect(hwnd, out bounds);
                var origin = new PointI();
                check(ClientToScreen(hwnd, ref origin), "Read glass client screen origin");
                check(client.Right == width && client.Bottom == height, "Glass client spans the complete actual HWND");
                check(origin.X == bounds.Left && origin.Y == bounds.Top, "Glass and HWND share the exact same top-left coordinate");
                check(GetWindowRgn(hwnd, region) == 0, "Glass never installs a GDI region that changes DWM's frame treatment");
                check(SetWindowPos(child, 0, 0, 0, width, height, 0x14), "Resize the full-client input child");
                foreach (var (cx, cy, hit) in new[] { (0, height / 2, 10), (width - 1, height / 2, 11),
                    (width / 2, 0, 12), (width / 2, height - 1, 15), (0, 0, 13), (width - 1, 0, 14),
                    (0, height - 1, 16), (width - 1, height - 1, 17) })
                {
                    nint position = unchecked((int)((uint)(ushort)(origin.Y + cy) << 16 | (ushort)(origin.X + cx)));
                    check(SendMessageW(child, 0x0084, 0, position) == hit,
                        "The content child returns a resize hit instead of swallowing it as transparent");
                    int before = sizingRequests;
                    SendMessageW(child, 0x00A1, (nuint)hit, position);
                    check(sizingRequests == before + 1 && sizingHit == (nuint)hit,
                        "A content-edge click reaches the parent sizing handler exactly once");
                }
                check(!frame.TryHandleContentMessage(hwnd, 0x0084, 0,
                    unchecked((int)((uint)(ushort)(origin.Y + height / 2) << 16 | (ushort)(origin.X + width / 2))), out _),
                    "Content input away from resize grips remains owned by WinUI");
                frame.SetEnabled(hwnd, false);
                check(!frame.TryHandleContentMessage(hwnd, 0x0084, 0, 0, out _), "Native mode releases the child resize handler");
                check(GetClientRect(hwnd, out var restored) && restored.Right < width && restored.Bottom < height,
                    "Native mode restores the conventional non-client frame");
            }
            check(SetWindowPos(hwnd, 0, 0, 0, 480, 600, 0x14), "Restore the headless window size");
            check(GetClientRect(hwnd, out var finalClient) && finalClient.Right == nativeClient.Right && finalClient.Bottom == nativeClient.Bottom,
                "Switching back restores the original client dimensions exactly");
            var finalOrigin = new PointI();
            check(ClientToScreen(hwnd, ref finalOrigin) && finalOrigin.X == nativeOrigin.X && finalOrigin.Y == nativeOrigin.Y,
                "Switching back restores the original client position exactly");
        }
        finally
        {
            RemoveWindowSubclass(child, contentCallback, 1); DestroyWindow(child);
            RemoveWindowSubclass(hwnd, callback, 1); DeleteObject(region); DestroyWindow(hwnd);
            GC.KeepAlive(callback); GC.KeepAlive(contentCallback);
        }
    }
}
