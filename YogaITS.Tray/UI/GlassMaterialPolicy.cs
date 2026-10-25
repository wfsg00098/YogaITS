namespace YogaITS.Tray.UI;

// Shader tint, not a foreground rectangle: every term reaches the glass body
// and the Fresnel/glare calculation. Channel units match the upstream brush.
internal readonly record struct GlassTint(double Red, double Green, double Blue, double Amount);

internal static class GlassMaterialPolicy
{
    public const byte SelectionTintAlpha = 15;
    public static GlassTint Tint(bool dark, bool windowBackground, GlassTint state, double? backgroundAmount = null)
    {
        double channel = windowBackground ? (dark ? 0 : 255) : (dark ? 24 : 250);
        double themeAmount = windowBackground ? Math.Clamp(backgroundAmount ?? BackgroundTuning.Default.TintAmount(dark), 0, 1) : 0.06;
        double amount = double.IsFinite(state.Amount) ? Math.Clamp(state.Amount, 0, 1) : 0;
        if (!windowBackground && amount > 0) return new(state.Red, state.Green, state.Blue, amount);
        double combined = amount + themeAmount * (1 - amount);
        if (combined == 0) return new(channel, channel, channel, 0);
        double Mix(double value) => (Math.Clamp(value, 0, 255) * amount + channel * themeAmount * (1 - amount)) / combined;
        return new(Mix(state.Red), Mix(state.Green), Mix(state.Blue), combined);
    }
}
