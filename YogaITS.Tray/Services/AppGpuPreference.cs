using Microsoft.Win32;
using System.Security;

namespace YogaITS.Tray.Services;

internal static class AppGpuPreference
{
    private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static void TrySetPowerSavingDefault()
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (key is null) return;
            object? existing = key.GetValue(executable);
            // Do not replace unexpected value types or an explicit user GPU choice.
            if (existing is not null && existing is not string) return;
            string? current = existing as string;
            string preference = PowerSavingDefault(current);
            if (!string.Equals(current, preference, StringComparison.Ordinal))
                key.SetValue(executable, preference, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            // This optional preference must never prevent the control center starting.
        }
    }

    internal static string PowerSavingDefault(string? current)
    {
        if (current is not null && current.Split(';').Any(setting =>
                string.Equals(setting.Split('=', 2)[0].Trim(), "GpuPreference", StringComparison.OrdinalIgnoreCase)))
            return current;

        // Preserve other per-app graphics options, such as Auto HDR and windowed optimizations.
        string prefix = string.IsNullOrEmpty(current) ? "" : current.EndsWith(';') ? current : current + ";";
        return prefix + "GpuPreference=1;";
    }
}
