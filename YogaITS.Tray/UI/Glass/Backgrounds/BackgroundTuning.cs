namespace YogaITS.Tray.UI.Glass.Backgrounds;

// Validated material defaults; independent from persisted user background choices.
internal readonly record struct BackgroundTuning(double LightTintPercent, double DarkTintPercent, double BlurDip)
{
    public const double DefaultBlurDip = 1;
    public static BackgroundTuning Default => new(40, 40, DefaultBlurDip);
    public double TintPercent(bool dark) => dark ? DarkTintPercent : LightTintPercent;
    public double TintAmount(bool dark) => TintPercent(dark) / 100;
    public BackgroundTuning WithTint(double value, bool dark) => !double.IsFinite(value) ? this : dark
        ? this with { DarkTintPercent = Math.Clamp(value, 0, 100) }
        : this with { LightTintPercent = Math.Clamp(value, 0, 100) };
    public BackgroundTuning WithBlur(double value) => double.IsFinite(value)
        ? this with { BlurDip = Math.Clamp(value, 0, 100) } : this;
    public double PhysicalBlur(double scale) => Math.Clamp(BlurDip *
        (double.IsFinite(scale) && scale > 0 ? scale : 1), 0, 250);
}
