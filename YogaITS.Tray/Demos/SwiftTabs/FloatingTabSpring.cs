using System.Numerics;

namespace YogaITS.Tray.Demos.SwiftTabs;

// Independent input, travel and inertia timings; scale duration is never shortened.
internal static class FloatingTabSpring
{
    public const int Samples = 24;
    public const float MinimumTravelSpeed = 1000 * (2f / 3); // DIPs per second, independent of DPI.
    public static TimeSpan TravelDuration => TimeSpan.FromMilliseconds(224);
    public static TimeSpan MovementDuration(Vector2 displacement)
    {
        double distance = displacement.Length();
        if (!double.IsFinite(distance)) return TimeSpan.Zero;
        double seconds = Math.Min(TravelDuration.TotalSeconds, distance / MinimumTravelSpeed);
        // Composition keyframes need at least 1 ms. Smaller moves snap instead
        // of introducing an artificial pause or lowering the minimum speed.
        return seconds < 0.001 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }
    public static TimeSpan GrowDuration => TimeSpan.FromMilliseconds(90);
    public static TimeSpan ShrinkDuration => TimeSpan.FromMilliseconds(90);
    public static TimeSpan InertiaDuration => TimeSpan.FromMilliseconds(420);
    public static TimeSpan EndAlignedDelay(TimeSpan movement, TimeSpan effect) => movement > effect ? movement - effect : TimeSpan.Zero;
    public static TimeSpan GrowthDelay(TimeSpan arrival) => EndAlignedDelay(arrival, GrowDuration);

    public static float Response(double time)
    {
        if (!double.IsFinite(time) || time <= 0) return 0;
        if (time >= 1) return 1;
        // Critically damped and normalized: approach the target without crossing it.
        const double frequency = 8;
        double end = 1 - (1 + frequency) * Math.Exp(-frequency);
        return (float)((1 - (1 + frequency * time) * Math.Exp(-frequency * time)) / end);
    }

    private static double ImpulsePeak => Math.Atan(Math.PI / 2) / Math.PI;
    private static double ImpulseNormalization => Math.Pow(Math.Sin(Math.PI * ImpulsePeak), 2) * Math.Exp(-4 * ImpulsePeak);
    public static float Impulse(double time)
    {
        if (!double.IsFinite(time) || time <= 0 || time >= 1) return 0;
        double sine = Math.Sin(Math.PI * time);
        return (float)(sine * sine * Math.Exp(-4 * time) / ImpulseNormalization);
    }

    private static float ResponseDerivative(double time)
    {
        if (time <= 0 || time >= 1) return 0;
        return (float)(64 * time * Math.Exp(-8 * time) / (1 - 9 * Math.Exp(-8)));
    }

    private static float ImpulseDerivative(double time)
    {
        if (time <= 0 || time >= 1) return 0;
        double sine = Math.Sin(Math.PI * time), cosine = Math.Cos(Math.PI * time);
        return (float)((2 * Math.PI * sine * cosine - 4 * sine * sine) * Math.Exp(-4 * time) / ImpulseNormalization);
    }

    // Weights for starting strain, velocity equilibrium, acceleration impulse,
    // and stopping impulse. Carry strain velocity across the planned arrival.
    public static Vector4 InertiaWeights(TimeSpan elapsed, TimeSpan? stopAt = null)
    {
        double seconds = InertiaDuration.TotalSeconds;
        if (stopAt is not TimeSpan stop || elapsed <= stop)
        {
            double t = elapsed.TotalSeconds / seconds;
            float response = Response(t);
            return new(1 - response, response, Impulse(t), 0);
        }
        double arrival = stop.TotalSeconds / seconds;
        double recoil = Math.Clamp((elapsed - stop).TotalSeconds / seconds, 0, 1);
        float settle = 1 - Response(recoil);
        float momentum = (float)(recoil * (1 - recoil) * (1 - recoil));
        float r = Response(arrival), impulse = Impulse(arrival);
        float rate = ResponseDerivative(arrival) * momentum;
        return new((1 - r) * settle - rate, r * settle + rate,
            impulse * settle + ImpulseDerivative(arrival) * momentum, Impulse(recoil));
    }
}
