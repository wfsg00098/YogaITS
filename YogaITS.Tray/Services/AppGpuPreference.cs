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

        TrySetDefault(executable, 1);
        TrySetDefault(Path.Combine(AppContext.BaseDirectory, GpuKeepAwakePolicy.HelperFileName), 2);
    }

    private static void TrySetDefault(string executable, int preferenceId)
    {

        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (key is null) return;
            object? existing = key.GetValue(executable);
            // Preserve unexpected value types. The UI preserves explicit choices;
            // the dedicated helper replaces only its own GPU preference below.
            if (existing is not null && existing is not string) return;
            string? current = existing as string;
            string preference = preferenceId == 2 ? HighPerformancePreference(current) : PowerSavingDefault(current);
            if (!string.Equals(current, preference, StringComparison.Ordinal))
                key.SetValue(executable, preference, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            // This optional preference must never prevent the control center starting.
        }
    }

    internal static string PowerSavingDefault(string? current) => GpuDefault(current, 1);

    internal static string HighPerformancePreference(string? current) => string.Join(';',
        (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(setting => !setting.Split('=', 2)[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase))
            .Append("GpuPreference=2")) + ";";

    internal static string GpuDefault(string? current, int preferenceId)
    {
        if (current is not null && current.Split(';').Any(setting =>
                string.Equals(setting.Split('=', 2)[0].Trim(), "GpuPreference", StringComparison.OrdinalIgnoreCase)))
            return current;

        // Preserve other per-app graphics options, such as Auto HDR and windowed optimizations.
        string prefix = string.IsNullOrEmpty(current) ? "" : current.EndsWith(';') ? current : current + ";";
        return prefix + $"GpuPreference={preferenceId};";
    }
}
