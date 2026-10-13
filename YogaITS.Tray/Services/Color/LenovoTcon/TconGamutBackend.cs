namespace YogaITS.Tray.Services.Color.LenovoTcon;

internal sealed record TconGamutSnapshot(bool Absent, bool Supported, string CurrentMode, IReadOnlyList<ColorMode> Modes);

internal static class TconGamutBackend
{
    public static TconGamutSnapshot Probe()
    {
        var lines = new List<string>();
        void Log(string message) => lines.Add(message);
        try
        {
            ProcessComSecurity.Log(Log);
            // COM and the mutex must be created, used and disposed on the same worker thread.
            using var wmi = new NativeTconWmi();
            wmi.LogPreflight(Log);
            if (!wmi.Ready)
                return new(wmi.HrGetClass == WbemNative.WBEM_E_NOT_FOUND, false, "未知", []);
            var ability = LenovoManagerPath.ReadAbility(wmi, Log);
            if (!ability.Success || (ability.AdjustedAbility & 0x08) == 0)
                return new(false, false, "未知", []);
            var current = LenovoManagerPath.ReadGamut(wmi, Log);
            if (!current.Success) return new(false, false, "未知", []);
            var modes = new byte[] { 0x06, 0x04, 0x08 }
                .Where(raw => LenovoManagerPath.SupportsMode(ability.AdjustedAbility, raw))
                .Select(raw => new ColorMode(ColorBackendPolicy.ModeId(raw), null)).ToArray();
            return new(false, modes.Length > 0, ColorBackendPolicy.ModeId(current.RawMode), modes);
        }
        catch (Exception ex)
        {
            Log(ex.GetType().Name + ": " + ex.Message);
            return new(false, false, "未知", []);
        }
        finally { ColorDiagnosticLog.Write("TCON capability probe", lines); }
    }

    public static void Apply(string mode)
    {
        var lines = new List<string>();
        try
        {
            // Repeat live WMI/capability/GET checks and verify the write up to six times.
            if (!LenovoManagerPath.SetGamut(ColorBackendPolicy.ModeRaw(mode), lines.Add))
                throw new InvalidOperationException("TCON 色域切换未通过能力检查或回读验证。");
        }
        finally { ColorDiagnosticLog.Write("TCON mode change", lines); }
    }
}
