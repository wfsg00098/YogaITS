using System.Numerics;
using YogaITS.Tray.Demos.Acceleration;
using YogaITS.Tray.Demos.SwiftTabs;

internal static class ReflowChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Physical reflow: " + message);
        }
        void Finite(FourEdgePhysics model)
        {
            var f = model.Frame;
            Check(float.IsFinite(f.X + f.Y + f.Width + f.Height) && f.Width > 1 && f.Height > 1,
                $"Changing geometry keeps finite ordered physical edges: {f}; target {model.Target}");
        }
        foreach (double width in new[] { 202.0, 352, 600, 960 })
        {
            var three = SwiftTabLayout.Create(width, 80, 3);
            var two = SwiftTabLayout.Create(width, 80, 2);
            var model = new FourEdgePhysics();
            model.Reset(three.Capsule(1));
            model.ConfigureTrack(three.Track, three.Capsule(0), three.Capsule(2));
            foreach (var layout in new[] { two, three, two, three, two })
            {
                var p = model.Position; var v = model.Velocity;
                model.ConfigureTrack(layout.Track, layout.Capsule(0), layout.Capsule(layout.Items.Length - 1), preserveFrame: true);
                model.SetTarget(layout.Capsule(1));
                Check(model.Position == p && model.Velocity == v,
                    "Rapid 3/2 option changes change targets without restarting the shape or momentum");
                for (int i = 0; i < 35; i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            }
            for (int i = 0; i < 1800; i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            Check(model.IsSettled && Math.Abs(model.Frame.Width - two.Capsule(1).Width) < 0.05,
                "Retargeted capsule finishes at the final two-option width");
        }
        foreach (var (from, target) in new[]
        {
            (new TabFrame(0,0,120,48), new TabFrame(0,0,360,48)),
            (new TabFrame(0,0,360,48), new TabFrame(0,0,80,72)),
            (new TabFrame(0,0,48,48), new TabFrame(350,250,48,48)),
            (new TabFrame(350,250,48,48), new TabFrame(-100,-60,48,48))
        })
        {
            var model = new FourEdgePhysics(); model.Reset(from);
            for (int turn = 0; turn < 5; turn++)
            {
                var p = model.Position; var v = model.Velocity;
                model.SetTarget(turn % 2 == 0 ? target : from);
                Check(model.Position == p && model.Velocity == v, "Card resize/reposition preserves its integrated state");
                for (int i = 0; i < 30; i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            }
            for (int i = 0; i < 2000; i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            Check(model.IsSettled && Math.Abs(model.Frame.X - target.X) < 0.03 && Math.Abs(model.Frame.Height - target.Height) < 0.05,
                "Both two-dimensional movement and shape changes settle completely");
        }
        {
            var wide = SwiftTabLayout.Create(960,80);
            var narrow = SwiftTabLayout.Create(202,80);
            var model = new FourEdgePhysics(); model.Reset(wide.Capsule(3));
            model.ConfigureTrack(wide.Track,wide.Capsule(0),wide.Capsule(3));
            var before = model.Position;
            model.ConfigureTrack(narrow.Track,narrow.Capsule(0),narrow.Capsule(3),preserveFrame:true);
            Check(model.Position == before, "Shrinking host bounds do not project an existing hull into place");
            model.SetTarget(narrow.Capsule(3));
            for (int i=0;i<2200;i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            Check(model.IsSettled && model.Frame.X+model.Frame.Width <= 208.001,
                "The spring returns to the new bounds and restores hard endpoint constraints");
        }
        // Visibility uses a shared neutral phase, including quick hide/show reversal.
        foreach (int hold in new[] { 1, 10, 40, 100 })
        {
            var model = new FourEdgePhysics(layoutOnly: true);
            model.Reset(new(1000,0,48,48));
            model.SetTarget(new(0,0,48,48));
            for (int i=0;i<hold;i++) model.Advance(FourEdgePhysics.FixedStep);
            var p=model.Position; var v=model.Velocity;
            model.SetTarget(new(1000,0,48,48));
            Check(model.Position==p && model.Velocity==v, "Reveal reversal retains current geometry and momentum");
            for (int i=0;i<1800;i++) { model.Advance(FourEdgePhysics.FixedStep); Finite(model); }
            Check(model.IsSettled && Math.Abs(model.Frame.X-1000)<0.05, "Shared reveal phase fully settles after reversal");
        }
        return checks;
    }
}
