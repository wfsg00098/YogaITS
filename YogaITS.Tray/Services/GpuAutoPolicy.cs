namespace YogaITS.Tray.Services;

internal static class GpuAutoPolicy
{
    public static int? DesiredTarget(int powerSource) => powerSource switch { 0 => 0, 1 => 1, _ => null };
    public static int? DesiredTarget(GpuWorkingMode preference, int powerSource) => preference switch
    {
        GpuWorkingMode.Default => 0,
        GpuWorkingMode.IGPUOnly => 1,
        GpuWorkingMode.Auto => DesiredTarget(powerSource),
        _ => null
    };
    public static bool ConflictingObservation(int wmiMode, bool nvidiaPresent, DgpuDisplayState display) =>
        wmiMode == 1 && (nvidiaPresent || display == DgpuDisplayState.Connected);

    public static int ConfirmedMode(int wmiMode, bool nvidiaPresent, DgpuDisplayState display) =>
        wmiMode is 0 or 1 && !ConflictingObservation(wmiMode, nvidiaPresent, display) ? wmiMode : -1;

    // Unknown power cannot choose an Auto target; every iGPU SET requires Disconnected.
    public static int? Target(int powerSource, DgpuDisplayState display) => Target(GpuWorkingMode.Auto, powerSource, display);
    public static int? Target(GpuWorkingMode preference, int powerSource, DgpuDisplayState display)
    {
        int? desired = DesiredTarget(preference, powerSource);
        return desired == 0 || desired == 1 && display == DgpuDisplayState.Disconnected ? desired : null;
    }

    public static bool ShouldRetry(GpuModeOperationResult result, int powerSource, GpuWorkingMode preference = GpuWorkingMode.Auto) =>
        DesiredTarget(preference, powerSource) == 1 && result.Target == 1 && result.Outcome == GpuModeOperationOutcome.Failed &&
        result.Display.State == DgpuDisplayState.Disconnected &&
        result.Failure?.Stage is GpuModeOperationStage.ModeSet or GpuModeOperationStage.EffectiveStateVerify &&
        result.AuxiliaryFailure is null;
}
