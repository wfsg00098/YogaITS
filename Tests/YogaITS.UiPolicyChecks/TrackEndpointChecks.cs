using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;
using System.Numerics;

internal static class TrackEndpointChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string message) { count++; if (!valid) throw new InvalidOperationException("Track endpoint: " + message); }
        void Inside(FourEdgePhysics model, float left, float right)
        {
            var rect = model.Frame;
            Check(float.IsFinite(rect.X + rect.Y + rect.Width + rect.Height) && rect.Width > 4 && rect.Height > 4,
                "collisions retain a finite ordered capsule");
            Check(rect.X >= left - 0.001 && rect.X + rect.Width <= right + 0.001,
                "the actual elastic hull stays inside both endpoint limits");
            if (model.Position.X <= left)
            {
                Check(model.Velocity.X >= 0, "the left wall absorbs outward velocity");
                if (model.Velocity.X == 0) Check(model.Acceleration.X >= 0, "a resting left contact cancels outward normal acceleration");
            }
            if (model.Position.Z >= right)
            {
                Check(model.Velocity.Z <= 0, "the right wall absorbs outward velocity");
                if (model.Velocity.Z == 0) Check(model.Acceleration.Z <= 0, "a resting right contact cancels outward normal acceleration");
            }
        }
        // Mirror the normal gaps through each track edge; asymmetric gaps also
        // check that the expansion is per side, not a scale based on full width.
        foreach (var (track, first, last) in new[]
        {
            (new TabFrame(0, 0, 600, 58), new TabFrame(5, 5, 140, 48), new TabFrame(455, 5, 140, 48)),
            (new TabFrame(10, 20, 600, 62), new TabFrame(17, 25, 140, 48), new TabFrame(459, 25, 140, 48))
        })
        {
            float gl = first.X - track.X, gt = first.Y - track.Y;
            float gr = track.X + track.Width - last.X - last.Width, gb = track.Y + track.Height - first.Y - first.Height;
            foreach (var normal in new[] { first, last })
            {
                var model = new FourEdgePhysics(); model.Reset(normal); model.ConfigureTrack(track, first, last);
                var p = model.Position; var v = model.Velocity;
                model.SetPressed(true);
                Check(model.Position == p && model.Velocity == v, "the new enlargement rule still changes forces only");
                for (int i = 0; i < 1600; i++) model.Advance(FourEdgePhysics.FixedStep);
                Check(model.IsSettled, "geometry-derived pressure reaches a stable target");
                Check(Math.Abs(model.Frame.X - (normal.X - 2 * gl)) < 0.08 &&
                    Math.Abs(model.Frame.Y - (normal.Y - 2 * gt)) < 0.08 &&
                    Math.Abs(model.Frame.Width - (normal.Width + 2 * (gl + gr))) < 0.08 &&
                    Math.Abs(model.Frame.Height - (normal.Height + 2 * (gt + gb))) < 0.08,
                    "a normal inner gap becomes the same outer overhang on each side");
            }
        }
        foreach (int width in new[] { 352, 680, 960 })
        foreach (int sign in new[] { -1, 1 })
        {
            var layout = ChoiceTrackLayout.Create(width, 80);
            var first = layout.Capsule(0); var last = layout.Capsule(3);
            float leftLimit = layout.Track.X - (first.X - layout.Track.X);
            float rightLimit = layout.Track.X + layout.Track.Width + (layout.Track.X + layout.Track.Width - last.X - last.Width);
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(1));
            model.ConfigureTrack(layout.Track, first, last); model.SetPressed(true);
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            model.DragTo(layout.Capsule(1), 0);
            bool hit = false, verticalOverhang = false;
            for (int sample = 1; sample <= 180; sample++)
            {
                var raw = layout.Capsule(1) with { X = layout.Capsule(1).X + sign * sample * 20 };
                var before = model.Position; var velocity = model.Velocity;
                model.DragTo(raw, sample / 120.0);
                Check(model.Position == before && model.Velocity == velocity,
                    "an outside pointer does not directly teleport the capsule");
                for (int substep = 0; substep < 2; substep++)
                {
                    model.Advance(FourEdgePhysics.FixedStep); Inside(model, leftLimit, rightLimit);
                }
                hit |= sign < 0 ? model.Frame.X <= leftLimit + 0.001 : model.Frame.X + model.Frame.Width >= rightLimit - 0.001;
                bool outsideReference = sign < 0 ? raw.X - 12 < leftLimit : raw.X + raw.Width + 12 > rightLimit;
                if (hit && outsideReference)
                {
                    Check(model.AppliedDragAcceleration.X * sign <= 0,
                        "continuing beyond a contacted endpoint cannot reapply outward input acceleration");
                    Check(model.EffectiveReferenceVelocity.X * sign <= 0,
                        "contact also disables outward reference-velocity drive");
                }
                verticalOverhang |= model.Frame.Height > layout.Track.Height + 1;
            }
            Check(hit, "the body reaches the track end when dragged far outside");
            Check(verticalOverhang, "horizontal limits do not suppress vertical enlargement or bulging");
            model.StopPointer();
            for (int i = 0; i < 1800; i++) { model.Advance(FourEdgePhysics.FixedStep); Inside(model, leftLimit, rightLimit); }
            Check(model.IsSettled, "an outside held pointer has a reachable goal and can stop rendering");
            var stopped = model.Position; var momentum = model.Velocity;
            model.SetPressed(false); model.SetTarget(sign < 0 ? first : last);
            Check(model.Position == stopped && model.Velocity == momentum,
                "release at an endpoint preserves integrated momentum");
            for (int i = 0; i < 1400; i++) { model.Advance(FourEdgePhysics.FixedStep); Inside(model, leftLimit, rightLimit); }
            Check(model.IsSettled, "endpoint release settles on its normal option");
            var smaller = ChoiceTrackLayout.Create(240, 80);
            model.ConfigureTrack(smaller.Track, smaller.Capsule(0), smaller.Capsule(3));
            model.SetTarget(smaller.Capsule(3));
            for (int i = 0; i < 1400; i++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                Inside(model, -6, 246);
            }
            Check(model.IsSettled, "resizing updates endpoint limits without invalid geometry");
        }
        // The gate is directional: outward input stays disabled after contact,
        // inward acceleration and the perpendicular component remain available.
        foreach (int sign in new[] { -1, 1 })
        {
            var layout = ChoiceTrackLayout.Create(680, 80);
            var model = new FourEdgePhysics(); model.Reset(layout.Capsule(1));
            model.ConfigureTrack(layout.Track, layout.Capsule(0), layout.Capsule(3)); model.SetPressed(true);
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            double inputTime = 0;
            var reference = layout.Capsule(1);
            model.DragTo(reference, inputTime);
            for (int i = 0; i < 180; i++)
            {
                inputTime += 1.0 / 120;
                reference = reference with { X = reference.X + sign * 20 };
                model.DragTo(reference, inputTime); model.Advance(1.0 / 120);
            }
            Check(model.ReferenceVelocity.X * sign > 1000 && model.EffectiveReferenceVelocity.X == 0 &&
                model.AppliedDragAcceleration.X == 0,
                "raw input sampling continues while the endpoint motor is stopped");
            Check(model.IsSettled, "an outward-moving pointer does not keep the contacted body artificially active");
            float centerAtWall = model.Frame.Center;
            inputTime += 1.0 / 120; reference = reference with { X = reference.X - sign * 20 };
            model.DragTo(reference, inputTime); model.Advance(1.0 / 120);
            Check(model.EffectiveReferenceVelocity.X * sign < 0 && model.AppliedDragAcceleration.X * sign < 0,
                "inward return acceleration is enabled immediately at either endpoint");
            Check((model.Frame.Center - centerAtWall) * sign < 0,
                "reversing away from the endpoint moves the body inward without sticking");
            inputTime += 1.0 / 120; reference = reference with { Y = reference.Y + 10 };
            model.DragTo(reference, inputTime); model.Advance(1.0 / 120);
            Check(model.EffectiveReferenceVelocity.Y > 0 && model.AppliedDragAcceleration.Y > 0,
                "horizontal drive blocking does not suppress perpendicular acceleration");
            model.StopPointer();
            for (int i = 0; i < 1200; i++) model.Advance(FourEdgePhysics.FixedStep);
            inputTime += 6;
            model.DragTo(layout.Capsule(1), inputTime); model.Advance(0.1);
            inputTime += 0.1;
            var inside = layout.Capsule(1) with { X = layout.Capsule(1).X + sign * 10 };
            model.DragTo(inside, inputTime); model.Advance(1.0 / 120);
            Check(model.EffectiveReferenceVelocity.X * sign > 0,
                "normal outward-direction input becomes available again inside the track");
        }
        // Endpoint walls have no effect on the already-approved interior feel.
        {
            var layout = ChoiceTrackLayout.Create(680, 80);
            var free = new FourEdgePhysics(); var bounded = new FourEdgePhysics();
            foreach (var model in new[] { free, bounded }) model.Reset(layout.Capsule(1));
            bounded.ConfigureTrack(layout.Track, layout.Capsule(0), layout.Capsule(3));
            foreach (var model in new[] { free, bounded }) { model.SetPressed(true); model.SetTarget(layout.Capsule(2)); }
            for (int i = 0; i < 1200; i++)
            {
                free.Advance(FourEdgePhysics.FixedStep); bounded.Advance(FourEdgePhysics.FixedStep);
                Check(Vector4.Distance(free.Position, bounded.Position) < 0.0001 && Vector4.Distance(free.Velocity, bounded.Velocity) < 0.0001,
                    "inside the track all edge positions and velocities keep the original physics");
            }
        }
        return count;
    }
}
