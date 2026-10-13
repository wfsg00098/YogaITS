namespace YogaITS.Tray.Services.Color.LenovoTcon;

internal sealed record TconGamutSnapshot(bool Absent, bool Supported, string CurrentMode, IReadOnlyList<ColorMode> Modes);

internal static class TconGamutBackend
{
    private static void Log(string message) => System.Diagnostics.Debug.WriteLine("TCON: " + message);

    public static TconGamutSnapshot Probe()
    {
        try
        {
            // COM and the mutex must be created, used and disposed on the same worker thread.
            using var wmi = new NativeTconWmi();
            if (!wmi.Ready)
                return new(wmi.HrGetClass == WbemNative.WBEM_E_NOT_FOUND, false, "未知", []);
            var ability = LenovoManagerPath.ReadAbility(wmi, Log);
            if (!ability.Success || (ability.AdjustedAbility & 0x08) == 0)
                return new(false, false, "未知", []);
            var current = LenovoManagerPath.ReadGamut(wmi, Log);
            if (!current.Success) return new(false, false, "未知", []);
            var modes = new byte[] { 0x06, 0x04, 0x08 }
                .Where(raw => ColorBackendPolicy.SupportsTconMode(ability.AdjustedAbility, raw))
                .Select(raw => new ColorMode(ColorBackendPolicy.ModeId(raw), null)).ToArray();
            return new(false, modes.Length > 0, ColorBackendPolicy.ModeId(current.RawMode), modes);
        }
        catch (Exception ex)
        {
            Log(ex.GetType().Name + ": " + ex.Message);
            return new(false, false, "未知", []);
        }
    }

    public static void Apply(string mode)
    {
        // SetGamut repeats live WMI/capability/GET checks and verifies the write up to six times.
        if (!LenovoManagerPath.SetGamut(ColorBackendPolicy.ModeRaw(mode), Log))
            throw new InvalidOperationException("TCON 色域切换未通过能力检查或回读验证。");
    }
}
