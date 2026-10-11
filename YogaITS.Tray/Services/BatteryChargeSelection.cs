namespace YogaITS.Tray.Services;

public readonly record struct BatterySelectionResult(
    bool Success, string Message, BatteryChargeSnapshot Charge, BatteryToggleSnapshot Night);

internal static class BatteryChargeSelection
{
    // Night is a fourth UI choice backed by night=true plus the normal charge mode.
    // Injected operations let the sequence be checked without touching EnergyDrv.
    public static async Task<BatterySelectionResult> ApplyAsync(
        BatteryChargeMode requested, bool night,
        Func<BatteryToggleSnapshot> readNight, Func<BatteryChargeSnapshot> readCharge,
        Func<bool, Task<BatteryToggleResult>> setNight,
        Func<BatteryChargeMode, Task<BatteryActionResult>> setMode)
    {
        BatteryChargeMode target = night ? BatteryChargeMode.Normal : requested;
        string error = "";
        bool nightSupported = false;
        try
        {
            var before = readNight();
            nightSupported = before.Supported;
            if (night && !nightSupported)
                throw new InvalidOperationException("当前设备无法使用夜间优化。");
            if (nightSupported)
            {
                // Keep the manager's ordering: enable night first, then set Normal.
                // Other choices explicitly disable night before setting their mode.
                var toggle = await setNight(night).ConfigureAwait(false);
                if (!toggle.Success) throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(toggle.Message) ? "夜间优化设置失败。" : toggle.Message);
            }
            var charge = await setMode(target).ConfigureAwait(false);
            if (!charge.Success) throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(charge.Message) ? "充电模式设置失败。" : charge.Message);
        }
        catch (Exception ex) { error = ex.Message; }

        var actualCharge = readCharge();
        var actualNight = readNight();
        bool confirmed = actualCharge.Supported && actualCharge.Mode == target &&
            (actualNight.Supported ? actualNight.Enabled == night : !nightSupported && !night);
        if (!confirmed && error.Length == 0) error = "充电设置未通过回读验证。";
        return new(error.Length == 0 && confirmed, error, actualCharge, actualNight);
    }
}
