using System.Numerics;
using YogaITS.Tray.Demos.Acceleration;
using YogaITS.Tray.Demos.SwiftTabs;

internal static class ButtonPressChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Card pressure: " + message);
        }
        foreach (float width in new[] { 64f, 165.5f, 300f, 600f })
        foreach (float height in new[] { 24f, 48f, 62f })
        {
            var rest = new TabFrame(0, 0, width, height);
            var model = new FourEdgePhysics(layoutOnly: true);
            model.Reset(rest);
            void Step()
            {
                model.Advance(FourEdgePhysics.FixedStep);
                var frame = model.Frame;
                Check(float.IsFinite(frame.X + frame.Y + frame.Width + frame.Height) && frame.Width > 4 && frame.Height > 4,
                    "Quick press/release retains finite ordered card edges");
                Check(Math.Abs(frame.Center - width / 2) < 0.001 && Math.Abs(frame.CenterY - height / 2) < 0.001,
                    "A stationary card inflates about its center without moving the label or layout");
            }
            foreach (int hold in new[] { 2, 8, 24, 5, 60 })
            {
                var before = model.Position; var velocity = model.Velocity;
                model.SetPressed(true);
                Check(before == model.Position && velocity == model.Velocity,
                    "Repeated presses change forces without resetting the spring");
                for (int i = 0; i < hold; i++) Step();
                before = model.Position; velocity = model.Velocity;
                model.SetPressed(false);
                Check(before == model.Position && velocity == model.Velocity,
                    "Release preserves the current shape and momentum");
                for (int i = 0; i < hold / 2 + 1; i++) Step();
            }
            model.SetPressed(true);
            for (int i = 0; i < 1200; i++) Step();
            Check(model.IsSettled && Math.Abs(model.Frame.Width - width - 24) < 0.05 &&
                Math.Abs(model.Frame.Height - height - 24) < 0.05,
                "All button sizes use the capsule's 12-DIP per-edge pressure expansion");
            model.SetPressed(false);
            for (int i = 0; i < 1200; i++) Step();
            Check(model.IsSettled && Math.Abs(model.Frame.Width - width) < 0.05 && Math.Abs(model.Frame.Height - height) < 0.05,
                "Released buttons return to their exact rest size and stop moving");
        }
        foreach (float startWidth in new[] { 80f, 180f, 350f })
        {
            var model = new FourEdgePhysics(layoutOnly: true);
            var original = new TabFrame(0, 0, startWidth, 48);
            model.Reset(original);
            foreach (float width in new[] { startWidth * 2, startWidth, startWidth * 2 })
            {
                model.SetTarget(original with { Width = width });
                for (int i = 0; i < 1000; i++)
                {
                    model.Advance(FourEdgePhysics.FixedStep);
                    Check(Math.Abs(model.Frame.X) < 0.0001 && Math.Abs(model.Frame.Y) < 0.0001 &&
                        Math.Abs(model.Frame.Height - 48) < 0.0001,
                        "A left-anchored row expansion moves only its right edge, never its fixed rear or perpendicular edges");
                }
                foreach (bool pressed in new[] { true, false, true, false })
                {
                    model.SetPressed(pressed);
                    for (int i = 0; i < 800; i++)
                    {
                        model.Advance(FourEdgePhysics.FixedStep);
                        Check(Math.Abs(model.Frame.Center - width / 2) < 0.001 && Math.Abs(model.Frame.CenterY - 24) < 0.001,
                            "A button remains centered after previous one-sided size changes");
                    }
                }
            }
        }
        // A fluid capsule also forgets old travel direction before an in-place press.
        {
            var model = new FourEdgePhysics();
            model.Reset(new(0,0,140,48)); model.SetTarget(new(180,0,140,48));
            for (int i=0;i<1600;i++) model.Advance(FourEdgePhysics.FixedStep);
            model.SetPressed(true);
            for (int i=0;i<800;i++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                Check(Math.Abs(model.Frame.Center-250)<0.001 && Math.Abs(model.Frame.CenterY-24)<0.001,
                    "A settled capsule inflates symmetrically after a directional selection move");
            }
        }
        return checks;
    }
}
