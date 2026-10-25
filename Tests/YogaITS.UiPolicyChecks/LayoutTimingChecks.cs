using YogaITS.Tray.Demos.Acceleration;
using YogaITS.Tray.Demos.SwiftTabs;

internal static class LayoutTimingChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool valid, string message)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Layout timing: " + message);
        }
        static double Arrival(FourEdgePhysics model, Func<TabFrame, bool> arrived)
        {
            for (int step = 1; step <= 2000; step++)
            {
                model.Advance(FourEdgePhysics.FixedStep);
                if (arrived(model.Frame)) return step * FourEdgePhysics.FixedStep;
            }
            throw new InvalidOperationException("Layout timing never arrived.");
        }
        var capsule = new FourEdgePhysics();
        capsule.Reset(new(0,0,140,48)); capsule.SetPressed(true);
        double reference = Arrival(capsule, frame => frame.Height >= 48 + 24 * 0.9);
        foreach (float width in new[] { 64f,140,300,600 })
        {
            var card = new FourEdgePhysics(layoutOnly:true);
            card.Reset(new(0,0,width,48)); card.SetPressed(true);
            double time = Arrival(card, frame => frame.Height >= 48 + 24 * 0.9);
            Check(Math.Abs(time-reference) <= 0.02, "Centered card inflation matches actual capsule inflation speed");
        }
        foreach (bool resize in new[] { true,false })
        {
            var card = new FourEdgePhysics(layoutOnly:true);
            card.Reset(new(0,0,140,48)); card.SetTarget(resize ? new(0,0,300,48) : new(160,0,140,48));
            double time = Arrival(card, frame => (resize ? frame.Width-140 : frame.X) >= 160*0.9);
            Check(Math.Abs(time-reference) <= 0.02, "Card resize and reposition use the same response as capsule inflation");
        }
        foreach (bool expanding in new[] { true,false })
        {
            var phase = new FourEdgePhysics(layoutOnly:true);
            phase.Reset(new(expanding ? 0 : 1000,0,48,48)); phase.SetHorizontalBounds(0,1048);
            phase.SetTarget(new(expanding ? 1000 : 0,0,48,48));
            double time=Arrival(phase,frame=>expanding ? frame.X>=900 : frame.X<=100);
            Check(Math.Abs(time-reference)<=0.02,
                "Reveal uses the same inflation response for every natural height");
        }
        return checks;
    }
}
