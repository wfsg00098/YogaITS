namespace YogaITS.Tray.Services;

internal enum UiBuildFlavor { Native, LiquidGlass }

internal static class AppBuild
{
#if LIQUID_GLASS
    public const UiBuildFlavor Flavor = UiBuildFlavor.LiquidGlass;
#else
    public const UiBuildFlavor Flavor = UiBuildFlavor.Native;
#endif
    public static string ExecutableName => ExecutableFor(Flavor);
    public static string ProcessName => Path.GetFileNameWithoutExtension(ExecutableName);
    public static string ExecutableFor(UiBuildFlavor flavor) => "YogaITS.Tray.exe";
    public static string ArchiveName(string version, UiBuildFlavor flavor) => flavor == UiBuildFlavor.LiquidGlass
        ? $"YogaITS-{version}-lg-win-x64.zip" : $"YogaITS-{version}-win-x64.zip";
}

internal static class AppIdentity
{
    public const string DisplayName = "Yoga控制中心";
    public static string TrayProcessName => AppBuild.ProcessName;
    public const string AppUserModelId = "YogaITS";
}
