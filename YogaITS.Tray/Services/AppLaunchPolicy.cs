namespace YogaITS.Tray.Services;

internal static class AppLaunchPolicy
{
    public static bool ShouldOpenControlCenter(IEnumerable<string> arguments) =>
        Has(arguments, "--restore-control-center") || !Has(arguments, "--startup");

    public static bool ShouldOpenMiniWindow(IEnumerable<string> arguments) => Has(arguments, "--restore-mini-window");

    public static string[] RecoveryArguments(bool controlCenter, bool miniWindow)
    {
        var arguments = new List<string> { "--startup" };
        if (controlCenter) arguments.Add("--restore-control-center");
        if (miniWindow) arguments.Add("--restore-mini-window");
        return arguments.ToArray();
    }

    private static bool Has(IEnumerable<string> arguments, string flag) =>
        arguments.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));
}
