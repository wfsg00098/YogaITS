using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;
using System.Numerics;

internal static class AccelerationChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string message) { count++; if (!valid) throw new InvalidOperationException("Acceleration: " + message); }
        void Finite(FourEdgePhysics physics)
        {
            var frame = physics.Frame;
            Check(float.IsFinite(frame.X + frame.Y + frame.Width + frame.Height) && frame.Width > 4 && frame.Height > 4,
                "four edges keep finite ordered bounds");
            Check(float.IsFinite(physics.Velocity.X + physics.Velocity.Y + physics.Velocity.Z + physics.Velocity.W),
                "velocities remain finite");
        }
        var layout = ChoiceTrackLayout.Create(680, 80);
        foreach (int original in Enumerable.Range(0, 4))
        foreach (int next in Enumerable.Range(0, 4))
        {
            var model = new FourEdgePhysics();
            model.Reset(layout.Capsule(original));
            var position = model.Position; var velocity = model.Velocity;
            model.SetPressed(true);
            model.SetTarget(layout.Capsule(next));
            Check(model.Position == position && model.Velocity == velocity,
                "pressing and targeting change forces without resetting edge position or velocity");
            float minWidth = float.MaxValue, maxWidth = 0;
            for (int step = 0; step < 1200; step++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                Finite(model);
                minWidth = Math.Min(minWidth, model.Frame.Width); maxWidth = Math.Max(maxWidth, model.Frame.Width);
            }
            Check(model.IsSettled, $"held movement {original}->{next} settles without a perpetual frame loop");
            Check(Math.Abs(model.Frame.Center - layout.Capsule(next).Center) < 0.05,
                "holding settles at the requested target");
            Check(Math.Abs(model.Frame.Width - (layout.Capsule(next).Width + 24)) < 0.08 &&
                Math.Abs(model.Frame.Height - 72) < 0.08,
                "press enlargement emerges from the same four edge states");
            Check(maxWidth - minWidth > 0.1, "pressure and movement produce physical shape changes");
            position = model.Position; velocity = model.Velocity;
            model.SetPressed(false);
            Check(model.Position == position && model.Velocity == velocity,
                "release preserves ongoing four edge motion");
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            Check(model.IsSettled && Math.Abs(model.Frame.Width - layout.Capsule(next).Width) < 0.08 &&
                Math.Abs(model.Frame.Height - 48) < 0.08, "release converges to the rest capsule");
        }
        // Visibility regression: a stable spring can still damp away all
        // deformation before contact. Verify a real launch stretch and impact,
        // including the free perpendicular bulge, in both travel directions.
        foreach (var (from, to) in new[] { (0, 1), (0, 3), (3, 2), (3, 0) })
        {
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(from));
            model.SetPressed(true); model.SetTarget(layout.Capsule(to));
            float restW = layout.Capsule(to).Width + 24, restH = 72;
            float stretch = 0, compression = float.MaxValue, bulge = 0, contactTime = -1, rearSpeed = 0;
            float leadingWall = to > from ? layout.Capsule(to).Center + restW / 2 : layout.Capsule(to).Center - restW / 2;
            for (int step = 0; step < 1200; step++)
            {
                bool hadContact = model.FrontContact;
                model.Advance(FourEdgePhysics.FixedStep);
                if (!model.FrontContact) stretch = Math.Max(stretch, model.Frame.Width);
                else
                {
                    if (!hadContact)
                    {
                        contactTime = (float)(step * FourEdgePhysics.FixedStep);
                        rearSpeed = Math.Abs(to > from ? model.Velocity.X : model.Velocity.Z);
                    }
                    compression = Math.Min(compression, model.Frame.Width);
                    bulge = Math.Max(bulge, model.Frame.Height);
                    float front = to > from ? model.Position.Z : model.Position.X;
                    Check(Math.Abs(front - leadingWall) < 0.001,
                        "the front stays at its wall while the rear compresses and the sides bulge");
                }
            }
            Check(stretch > restW * 1.2f, "moving acceleration produces visible launch stretch beyond ordinary press growth");
            Check(contactTime > 0.15f && contactTime < 0.25f && rearSpeed > 100,
                "slower physical movement takes longer to arrive while retaining useful compression momentum");
            Check(compression < restW * 0.94f && bulge > restH * 1.04f,
                "impact visibly compresses the longitudinal span and expands the perpendicular span");
            Check(model.IsSettled, "stronger launch and impact still stop rendering after settling");
        }
        // Drag input changes only the common force reference. All physical
        // positions and world velocities remain continuous, with no centering
        // or leading-edge constraints during this experiment.
        foreach (var direction in new[] { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY, new Vector2(0.6f, 0.8f) })
        {
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(0)); model.SetPressed(true);
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            var start = layout.Capsule(0);
            double sampleTime = 0;
            float distance = 0, maxLag = 0;
            bool sawAppliedAcceleration = false;
            model.DragTo(start, sampleTime);
            for (int step = 0; step < 60; step++)
            {
                float speed = step < 12 ? 1000 * (step + 1) / 12f : step < 48 ? 1000 : 1000 * (60 - step) / 12f;
                sampleTime += 1.0 / 120;
                distance += speed / 120;
                var frame = start with { X = start.X + direction.X * distance, Y = start.Y + direction.Y * distance };
                var p = model.Position; var v = model.Velocity;
                model.DragTo(frame, sampleTime);
                Check(model.Position == p && model.Velocity == v,
                    "pointer input changes only the common reference, never recenters or resets world momentum");
                Check(Vector2.Distance(model.ReferenceVelocity, direction * speed) < 0.1,
                    "pointer velocity comes from consecutive input samples, independently of visual lag");
                model.Advance(1.0 / 120); Finite(model);
                var referenceCenter = new Vector2(frame.Center, frame.Y + frame.Height / 2);
                var actualCenter = new Vector2(model.Frame.Center, model.Frame.Y + model.Frame.Height / 2);
                float lag = Vector2.Dot(referenceCenter - actualCenter, direction);
                maxLag = Math.Max(maxLag, lag);
                sawAppliedAcceleration |= Vector2.Dot(model.AppliedDragAcceleration, direction) > 1;
                Check(float.IsFinite(model.AppliedDragAcceleration.X + model.AppliedDragAcceleration.Y),
                    "signed two-dimensional drag acceleration remains finite throughout acceleration and braking");
                float cross = model.AppliedDragAcceleration.X * direction.Y - model.AppliedDragAcceleration.Y * direction.X;
                Check(Math.Abs(cross) < Math.Max(1, model.AppliedDragAcceleration.Length() * 0.001f),
                    "common acceleration preserves the signed input vector without a perpendicular artificial kick");
            }
            Check(maxLag > 2 && maxLag < 100 && sawAppliedAcceleration,
                "input acceleration drives visible common inertial lag without a position constraint");
            var positionAtStop = model.Position; var velocityAtStop = model.Velocity;
            model.StopPointer();
            Check(model.Position == positionAtStop && model.Velocity == velocityAtStop,
                "stopping the hand preserves the capsule's existing momentum");
            model.Advance(FourEdgePhysics.FixedStep);
            var delta = new Vector2((model.Position.X + model.Position.Z - positionAtStop.X - positionAtStop.Z) / 2,
                (model.Position.Y + model.Position.W - positionAtStop.Y - positionAtStop.W) / 2);
            Check(Vector2.Dot(delta, direction) > 0, "the body continues briefly through inertia when pointer motion stops");
            for (int i = 0; i < 1600; i++) model.Advance(FourEdgePhysics.FixedStep);
            Check(model.IsSettled, "lag and stopping recoil eventually settle and sleep");
        }
        {
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(0)); model.SetPressed(true);
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            model.DragTo(layout.Capsule(0), 0);
            for (int i = 1; i <= 24; i++)
            {
                model.DragTo(layout.Capsule(0) with { X = 6 + i * 1000 / 120f }, i / 120.0);
                model.Advance(1.0 / 120);
            }
            var p = model.Position; var v = model.Velocity;
            Check((v.X + v.Z) / 2 > 100, "release test starts with real world-space momentum");
            model.SetPressed(false); model.SetTarget(layout.Capsule(2));
            Check(!model.Dragging && model.Position == p && model.Velocity == v,
                "release carries actual velocity without adding pointer velocity a second time");
            for (int i = 0; i < 1600; i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            Check(model.IsSettled, "inertial drag release still converges to the snapped option");
        }
        foreach (var (from, to) in new[] { (0, 1), (3, 2) })
        {
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(from)); model.SetPressed(true); model.SetTarget(layout.Capsule(to));
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            Check(model.FrontContact, "size-change test begins at a settled contacted wall");
            var before = model.Position;
            model.SetPressed(false); model.Advance(FourEdgePhysics.FixedStep);
            Check(Vector4.Distance(model.Position, before) < 1,
                "shrinking the goal cannot teleport an already-outside edge to its new wall");
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            Check(model.IsSettled, "an inward-moving boundary returns smoothly to rest");
        }
        {
            var sample = new FourEdgePhysics(); var initial = layout.Capsule(0); sample.Reset(initial);
            sample.DragTo(initial, 0);
            sample.DragTo(initial with { X = initial.X + 4, Y = initial.Y + 3 }, 0.001);
            Check(Vector2.Distance(sample.ReferenceVelocity, new(2400, 1800)) < 1,
                "velocity limiting preserves the full vector direction and limits magnitude");
            var prior = sample.ReferenceVelocity;
            sample.DragTo(initial with { X = initial.X + 4.01f, Y = initial.Y + 3.01f }, 0.00101);
            Check(sample.ReferenceVelocity == prior, "very close input timestamps accumulate rather than injecting a false stop");
        }
        // iOS-like steady motion retains viscous lag, rather than converging
        // onto the pointer or accumulating an ever-growing following distance.
        foreach (float sign in new[] { 1f, -1f })
        {
            var model = new FourEdgePhysics(); var first = layout.Capsule(0);
            model.Reset(first); model.SetPressed(true);
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            model.DragTo(first, 0);
            float minLag = float.MaxValue, maxLag = float.MinValue;
            for (int step = 1; step <= 240; step++)
            {
                var movingReference = first with { X = first.X + sign * 1000 * step / 120f };
                model.DragTo(movingReference, step / 120.0); model.Advance(1.0 / 120);
                Finite(model);
                if (step >= 120)
                {
                    float lag = sign * (movingReference.Center - model.Frame.Center);
                    minLag = Math.Min(minLag, lag); maxLag = Math.Max(maxLag, lag);
                }
            }
            Check(minLag > 2 && maxLag < 60 && maxLag - minLag < 1,
                "steady 1000 DIP/s drag retains a stable, moderate viscous following distance");
            var before = model.Position; var momentum = model.Velocity;
            var oldTarget = model.Target;
            var reversal = oldTarget with { X = oldTarget.X - sign * 50 };
            model.DragTo(reversal, 241 / 120.0);
            Check(model.Position == before && model.Velocity == momentum,
                "an abrupt reversal cannot teleport the body or reset its existing world velocity");
            model.Advance(FourEdgePhysics.FixedStep);
            Check(sign * (model.Frame.Center - (before.X + before.Z) / 2) > 0,
                "reversal retains forward momentum for a finite time instead of clipping to the new reference");
            model.StopPointer();
            for (int i = 0; i < 1600; i++) model.Advance(FourEdgePhysics.FixedStep);
            Check(model.IsSettled, "unconstrained reversal and recoil still dissipate and sleep");
        }
        var rapid = new FourEdgePhysics(); rapid.Reset(layout.Capsule(0));
        for (int action = 0; action < 100; action++)
        {
            var p = rapid.Position; var v = rapid.Velocity;
            rapid.SetPressed(action % 2 == 0);
            rapid.SetTarget(layout.Capsule(action % 4));
            Check(rapid.Position == p && rapid.Velocity == v, "rapid reversals preserve the previous physical state");
            rapid.Advance(0.025); Finite(rapid);
        }
        var drag = new FourEdgePhysics(); drag.Reset(layout.Capsule(0)); drag.SetPressed(true);
        double time = 0;
        for (int i = 0; i < 120; i++)
        {
            float x = 120 + 80 * MathF.Sin(i * 0.15f);
            var frame = new TabFrame(x, 16, layout.Capsule(0).Width, 48);
            time += 1.0 / 120;
            var priorPosition = drag.Position; var priorVelocity = drag.Velocity;
            drag.DragTo(frame, time);
            Check(drag.Position == priorPosition && drag.Velocity == priorVelocity,
                "rapid drag reversals preserve both position and momentum");
            Check(drag.Target == frame, "the captured pointer updates its reference immediately");
            drag.Advance(1.0 / 120); Finite(drag);
        }
        drag.StopPointer();
        for (int i = 0; i < 1600; i++) drag.Advance(FourEdgePhysics.FixedStep);
        Check(drag.IsSettled, "drag-idle damping eventually sleeps the simulation");
        var releasePosition = drag.Position;
        drag.SetPressed(false); drag.SetTarget(layout.Capsule(2));
        Check(!drag.Dragging && drag.Position == releasePosition, "releasing a drag retargets without a position jump");
        for (int i = 0; i < 1600; i++) { drag.Advance(FourEdgePhysics.FixedStep); Finite(drag); }
        Check(drag.IsSettled, "a drag release settles on the snapped option");
        Vector4? reference = null;
        foreach (int rate in new[] { 30, 60, 120, 240 })
        {
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(0)); model.SetPressed(true); model.SetTarget(layout.Capsule(3));
            for (int i = 0; i < rate; i++) model.Advance(1.0 / rate);
            reference ??= model.Position;
            Check(Vector4.Distance(reference.Value, model.Position) < 0.001,
                "fixed substeps give the same trajectory at different rendering rates");
        }
        var stalled = new FourEdgePhysics(); var bounded = new FourEdgePhysics();
        foreach (var model in new[] { stalled, bounded }) { model.Reset(layout.Capsule(0)); model.SetTarget(layout.Capsule(3)); }
        stalled.Advance(10); bounded.Advance(0.1);
        Check(stalled.Position == bounded.Position, "stalled rendering drops excess time instead of accumulating a frame backlog");
        return count;
    }
}
