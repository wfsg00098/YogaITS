namespace YogaITS.Tray.Services.Color.LenovoTcon;

internal sealed record TconGamutSnapshot(bool Absent, bool Supported, string CurrentMode, IReadOnlyList<ColorMode> Modes,
    bool Available = true);

internal static class TconGamutBackend
{
    // Preserve the probe's transport callback contract without persisting diagnostics.
    private static void IgnoreDiagnostic(string message) { }

    public static TconGamutSnapshot Probe()
    {
        try
        {
            // COM and the mutex must be created, used and disposed on the same worker thread.
            using var wmi = new NativeTconWmi();
            if (!wmi.Ready)
                return new(wmi.HrGetClass == WbemNative.WBEM_E_NOT_FOUND, false, "未知", [],
                    wmi.HrGetClass == WbemNative.WBEM_E_NOT_FOUND);
            var ability = LenovoManagerPath.ReadAbility(wmi, IgnoreDiagnostic);
            if (!ability.Success) return new(false, false, "未知", [], false);
            if ((ability.AdjustedAbility & 0x08) == 0)
                return new(false, false, "未知", []);
            var current = LenovoManagerPath.ReadGamut(wmi, IgnoreDiagnostic);
            if (!current.Success) return new(false, false, "未知", [], false);
            var modes = new byte[] { 0x06, 0x04, 0x08 }
                .Where(raw => LenovoManagerPath.SupportsMode(ability.AdjustedAbility, raw))
                .Select(raw => new ColorMode(ColorBackendPolicy.ModeId(raw), null)).ToArray();
            return new(false, modes.Length > 0, ColorBackendPolicy.ModeId(current.RawMode), modes);
        }
        catch
        {
            return new(false, false, "未知", [], false);
        }
    }

    public static void Apply(string mode)
    {
        // Repeat live WMI/capability/GET checks and verify the write up to six times.
        if (!LenovoManagerPath.SetGamut(ColorBackendPolicy.ModeRaw(mode), IgnoreDiagnostic))
            throw new InvalidOperationException("TCON 色域切换未通过能力检查或回读验证。");
    }
}
