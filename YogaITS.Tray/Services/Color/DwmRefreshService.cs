namespace YogaITS.Tray.Services.Color;

using System.Diagnostics;

internal sealed record DwmRefreshResult(bool Ok, int ReturnCode, long ElapsedMs, string Message);

internal static class DwmRefreshService
{
    // Reapply current registered display settings with flags=0. On the validation machine
    // this reliably caused DWM to reload the active ICC/MHC state with no visible blink.
    public static DwmRefreshResult RefreshCurrentDisplayConfiguration()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            int rc = NativeMethods.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            sw.Stop();
            return rc == 0
                ? new(true, rc, sw.ElapsedMilliseconds,
                    "ChangeDisplaySettingsExW(NULL,NULL,flags=0) PASS; DWM display-color state refresh requested.")
                : new(false, rc, sw.ElapsedMilliseconds,
                    $"ChangeDisplaySettingsExW failed: DISP_CHANGE={rc}.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new(false, int.MinValue, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
