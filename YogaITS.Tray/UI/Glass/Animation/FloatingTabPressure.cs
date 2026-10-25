using System.Globalization;
using System.Numerics;

namespace YogaITS.Tray.UI.Glass.Animation;

// One native phase clock: X=time, Y=recovery start (-1 while held),
// Z=growth baseline, W=growth delay. Event retargets capture the compositor's
// StartingValue, so release neither restarts growth nor waits for a UI callback.
internal static class FloatingTabPressure
{
    private static float Grow => (float)FloatingTabSpring.GrowDuration.TotalSeconds;
    private static float Shrink => (float)FloatingTabSpring.ShrinkDuration.TotalSeconds;
    public static TimeSpan ClockDuration => FloatingTabSpring.TravelDuration +
        FloatingTabSpring.GrowDuration + FloatingTabSpring.ShrinkDuration;
    public static Vector4 Rest => new(Grow + Shrink, Grow, 0, 0);
    public static float Ease(float progress) => (1 - MathF.Cos(MathF.PI * Math.Clamp(progress, 0, 1))) / 2;
    public static float Pulse(Vector4 phase)
    {
        float growth = phase.Z + (1 - phase.Z) * Ease((phase.X - phase.W) / Grow);
        return growth * (phase.Y < 0 ? 1 : 1 - Ease((phase.X - phase.Y) / Shrink));
    }

    public static Vector4 Begin(Vector4 phase, bool pressed, TimeSpan movement)
    {
        if (pressed)
        {
            bool growing = phase.X < phase.W + Grow;
            return new(growing ? phase.X : 0, -1, growing ? phase.Z : Pulse(phase),
                growing ? phase.W : (float)FloatingTabSpring.GrowthDelay(movement).TotalSeconds);
        }
        return new(phase.X, phase.Y >= 0 ? phase.Y : Math.Max(phase.W + Grow,
            phase.X + (float)FloatingTabSpring.EndAlignedDelay(movement, FloatingTabSpring.ShrinkDuration).TotalSeconds),
            phase.Z, phase.W);
    }

    private static string N(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static string Smooth(string value) => "(0.5-0.5*Cos(Pi*Clamp(" + value + ",0,1)))";
    public static string PulseExpression(string phase) => "(" + phase + ".Z+(1-" + phase + ".Z)*" +
        Smooth("(" + phase + ".X-" + phase + ".W)/" + N(Grow)) + ")*(" + phase + ".Y<0 ? 1 : 1-" +
        Smooth("(" + phase + ".X-" + phase + ".Y)/" + N(Shrink)) + ")";

    public static string PhaseExpression(bool pressed, TimeSpan movement, bool end)
    {
        const string p = "this.StartingValue";
        string x, y, z, w;
        if (pressed)
        {
            string growing = p + ".X<" + p + ".W+" + N(Grow);
            x = "(" + growing + " ? " + p + ".X : 0)";
            y = "-1";
            z = "(" + growing + " ? " + p + ".Z : " + PulseExpression(p) + ")";
            w = "(" + growing + " ? " + p + ".W : " + N(FloatingTabSpring.GrowthDelay(movement).TotalSeconds) + ")";
        }
        else
        {
            x = p + ".X";
            y = "(" + p + ".Y>=0 ? " + p + ".Y : Max(" + p + ".W+" + N(Grow) + "," +
                p + ".X+" + N(FloatingTabSpring.EndAlignedDelay(movement, FloatingTabSpring.ShrinkDuration).TotalSeconds) + "))";
            z = p + ".Z"; w = p + ".W";
        }
        if (end) x += "+" + N(ClockDuration.TotalSeconds);
        return "Vector4(" + x + "," + y + "," + z + "," + w + ")";
    }
}
