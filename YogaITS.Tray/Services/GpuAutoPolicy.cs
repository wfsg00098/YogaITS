namespace YogaITS.Tray.Services;

internal static class GpuAutoPolicy
{
    public static int? DesiredTarget(int powerSource) => powerSource switch { 0 => 0, 1 => 1, _ => null };
    public static bool ConflictingObservation(int wmiMode, bool nvidiaPresent, DgpuDisplayState display) =>
        wmiMode == 1 && (nvidiaPresent || display == DgpuDisplayState.Connected);

    public static int ConfirmedMode(int wmiMode, bool nvidiaPresent, DgpuDisplayState display) =>
        wmiMode is 0 or 1 && !ConflictingObservation(wmiMode, nvidiaPresent, display) ? wmiMode : -1;

    // Unknown power or NVAPI display state must never request iGPU-only.
    public static int? Target(int powerSource, DgpuDisplayState display) => powerSource switch
    {
        0 => 0,
        1 when display == DgpuDisplayState.Disconnected => 1,
        _ => null
    };

    public static bool ShouldRetry(GpuModeOperationResult result, int powerSource) =>
        powerSource == 1 && result.Target == 1 && result.Outcome == GpuModeOperationOutcome.Failed &&
        result.Display.State == DgpuDisplayState.Disconnected &&
        result.Failure?.Stage is GpuModeOperationStage.ModeSet or GpuModeOperationStage.EffectiveStateVerify &&
        result.AuxiliaryFailure is null;
}
