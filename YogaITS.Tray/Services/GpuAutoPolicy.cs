namespace YogaITS.Tray.Services;

internal static class GpuAutoPolicy
{
    // Unknown power or display-path state must never request iGPU-only.
    public static int? Target(int powerSource, int externalPath) => powerSource switch
    {
        0 => 0,
        1 when externalPath == 0 => 1,
        _ => null
    };
}
