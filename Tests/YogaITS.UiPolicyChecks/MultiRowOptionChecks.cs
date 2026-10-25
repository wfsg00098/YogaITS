using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;
using System.Numerics;

internal static class MultiRowOptionChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Multirow option bar: " + message);
        }
        foreach (double width in new[] { 352.0, 420, 600, 960 })
        {
            var layout = ChoiceTrackLayout.Create(width, ChoiceTrackLayout.NaturalHeight(7, 2), 7, 2);
            var first = layout.Capsule(0);
            var last = layout.Capsule(6);
            Check(layout.Rows == 4 && layout.Columns == 2 && layout.Track.Height == 240,
                "Seven options share a single four-row track");
            Check(last == layout.Items[6] && last.Width == width - 12 && last.X == first.X,
                "The seventh capsule uses the entire final row, with no four-slot clamp");
            Check(layout.Hit(last.X + 1, last.CenterY) == 6 && layout.Hit(last.X + last.Width - 1, last.CenterY) == 6,
                "The whole wide final row selects factory default");
            Check(layout.VerticalNeighbor(1, 1) == 3 && layout.VerticalNeighbor(3, 1) == 5 &&
                layout.VerticalNeighbor(5, 1) == 6 && layout.VerticalNeighbor(6, -1) == 4 &&
                layout.VerticalNeighbor(0, -1) == 0 && layout.VerticalNeighbor(6, 1) == 6,
                "Vertical keyboard navigation keeps the nearest column and handles the wide final row");
            for (int index = 0; index < 7; index++)
            {
                var item = layout.Capsule(index);
                var point = new Vector2(item.Center, item.CenterY);
                Check(layout.Hit(point.X, point.Y) == index, "Every option has its own two-dimensional hit target");
                var click = new FloatingTabGesture();
                Check(click.Begin(1, point, 6, layout) && click.ConfirmedIndex == 6, "Press only previews a color choice");
                Check(click.Release(1, point, out int clicked) && clicked == index, "Every row supports click selection");
                var gesture = new FloatingTabGesture();
                Check(gesture.Begin(2, new Vector2(last.Center, last.CenterY), 6, layout), "Drag can begin on factory default");
                Check(!gesture.Move(3, point, out _), "A different pointer cannot take over a cross-row drag");
                gesture.Move(2, point, out _);
                Check(gesture.ConfirmedIndex == 6, "Two-dimensional dragging never commits early");
                Check(gesture.Release(2, point, out int released) && released == index, "Release can select all seven rows/columns");
            }
            // Exercise variable-width and diagonal motion, including both directions
            // between the full-width bottom capsule and a half-width top capsule.
            foreach (int from in new[] { 0, 3, 6 })
            foreach (int to in new[] { 0, 1, 4, 5, 6 })
            {
                var model = new FourEdgePhysics();
                model.Reset(layout.Capsule(from));
                model.ConfigureTrack(layout.Track, first, last, constrainVertical: true);
                model.SetPressed(true);
                model.SetTarget(layout.Capsule(to));
                for (int step = 0; step < 900; step++)
                {
                    model.Advance(FourEdgePhysics.FixedStep);
                    var frame = model.Frame;
                    Check(float.IsFinite(frame.X + frame.Y + frame.Width + frame.Height) && frame.Width > 4 && frame.Height > 4 &&
                        frame.X >= -6.001 && frame.X + frame.Width <= width + 6.001 &&
                        frame.Y >= layout.Track.Y - 6.001 && frame.Y + frame.Height <= layout.Track.Y + layout.Track.Height + 6.001,
                        "Diagonal and changing-width motion retains finite geometry within all four limits");
                }
                model.SetPressed(false);
                for (int step = 0; step < 1200; step++) model.Advance(FourEdgePhysics.FixedStep);
                var target = layout.Capsule(to);
                Check(model.IsSettled && Math.Abs(model.Frame.X - target.X) < 0.03 && Math.Abs(model.Frame.Y - target.Y) < 0.03 &&
                    Math.Abs(model.Frame.Width - target.Width) < 0.03, "Release settles on the correct row and width");
            }
            foreach (int sign in new[] { -1, 1 })
            {
                var model = new FourEdgePhysics();
                model.Reset(layout.Capsule(3));
                model.ConfigureTrack(layout.Track, first, last, constrainVertical: true);
                model.SetPressed(true);
                var input = layout.Capsule(3);
                model.DragTo(input, 0);
                for (int i = 1; i <= 180; i++)
                {
                    input = input with { Y = input.Y + sign * 20 };
                    model.DragTo(input, i / 120.0);
                    model.Advance(1.0 / 120);
                }
                Check(model.ReferenceVelocity.Y * sign > 1000 && model.EffectiveReferenceVelocity.Y == 0 && model.AppliedDragAcceleration.Y == 0,
                    "Top/bottom contact cancels only outward input drive");
                input = input with { Y = input.Y - sign * 20 };
                model.DragTo(input, 181.0 / 120);
                model.Advance(1.0 / 120);
                Check(model.EffectiveReferenceVelocity.Y * sign < 0 && model.AppliedDragAcceleration.Y * sign < 0,
                    "Vertical inward input becomes available immediately");
            }
            var cancelled = new FloatingTabGesture();
            cancelled.Begin(7, new Vector2(first.Center, first.CenterY), 0, layout);
            cancelled.Move(7, new Vector2(last.Center, last.CenterY), out _);
            cancelled.Cancel();
            Check(!cancelled.Release(7, new Vector2(last.Center, last.CenterY), out _) && cancelled.ConfirmedIndex == 0,
                "Cancelling or switching to native cards never commits the preview");
        }
        return checks;
    }
}
