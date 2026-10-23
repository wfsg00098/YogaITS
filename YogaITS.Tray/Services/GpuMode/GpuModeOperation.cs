using System.Diagnostics;
using System.Management;

namespace YogaITS.Tray.Services;

internal enum DgpuDisplayState { Disconnected, Connected, Unknown }
internal enum GpuModeOperationStage
{
    RuntimeStateRead, DisplayGuard, KeepAwakeSuspend, ModeSet, ModeReadback, EffectiveStateVerify, KeepAwakeRestore
}
internal enum GpuModeOperationOutcome { Success, SuccessWithWarning, Blocked, Failed, Indeterminate }

internal sealed record DgpuDisplayQueryResult(DgpuDisplayState State, string Backend = "NVAPI",
    int? NativeCode = null, string? Detail = null);

internal sealed record GpuModeOperationFailure(GpuModeOperationStage Stage, string Backend, string Summary,
    string? Detail = null, int? NativeCode = null, string? ExceptionType = null)
{
    public static GpuModeOperationFailure From(GpuModeOperationStage stage, string backend, string summary, Exception ex) =>
        new(stage, backend, summary, ex.Message,
            ex is ManagementException management ? (int)management.ErrorCode : ex.HResult, ex.GetType().FullName);
}

internal sealed record GpuModeOperationResult(int Target, GpuModeOperationOutcome Outcome, int? RawWmiMode,
    bool SetInvoked, DgpuDisplayQueryResult Display, GpuModeOperationFailure? Failure,
    GpuModeOperationFailure? AuxiliaryFailure, bool? NvidiaPresent)
{
    public int? EffectiveMode => RawWmiMode is { } raw && NvidiaPresent is { } present &&
        GpuAutoPolicy.ConfirmedMode(raw, present, Display.State) is 0 or 1 ? raw : null;
    public bool Succeeded => Outcome is GpuModeOperationOutcome.Success or GpuModeOperationOutcome.SuccessWithWarning;
    public string Message => Failure?.Summary ?? AuxiliaryFailure?.Summary ?? "";
}

internal static class GpuModeDiagnostics
{
    public static void Write(string message) => Trace.WriteLine("[GPU Mode] " + message);
}

internal sealed class GpuModeOperationException(GpuModeOperationResult result) : InvalidOperationException(result.Message)
{
    public GpuModeOperationResult Result { get; } = result;
}
