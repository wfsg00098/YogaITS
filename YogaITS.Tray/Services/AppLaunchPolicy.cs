namespace YogaITS.Tray.Services;

internal static class AppLaunchPolicy
{
    public static bool ShouldOpenControlCenter(IEnumerable<string> arguments) =>
        !arguments.Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
}
