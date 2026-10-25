using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.UI;
using YogaITS.Tray.Services.Updates;

int packageIndex = Array.IndexOf(args, "--check-package");
if (packageIndex >= 0)
{
    string source = Path.GetFullPath(args[packageIndex + 1]);
    using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "build-flavor.json")));
    UiBuildFlavor flavor = marker.RootElement.GetProperty("flavor").GetString() switch
    {
        "native" => UiBuildFlavor.Native,
        "liquid-glass" => UiBuildFlavor.LiquidGlass,
        _ => throw new InvalidDataException("Unknown build flavor.")
    };
    string stage = Path.Combine(Path.GetTempPath(), "YogaITS-Package-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stage);
    try
    {
        string archive = Path.Combine(stage, "release.zip");
        ZipFile.CreateFromDirectory(source, archive);
        CheckArchiveSize(new FileInfo(archive).Length);
        UpdatePackage.ExtractAndVerify(archive, Path.Combine(stage, "verified"),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive))), flavor);
        Console.WriteLine($"PASS: complete {flavor} publish directory survives ZIP extraction and updater verification.");
    }
    finally { Directory.Delete(stage, recursive: true); }
    return;
}

if (args.Contains("--check-updates-http"))
{
    using var client = new GitHubUpdateClient();
    var release = await client.GetLatestAsync(default);
    Console.WriteLine($"Latest stable release: {release.Version}; bytes={release.Size}; SHA256 present. No download, install, or hardware access.");
    return;
}

// Opt-in process-tree smoke test uses only child processes created by this test.
if (args.Length == 1 && args[0] == "--gpu-test-child")
{
    await Task.Delay(Timeout.Infinite);
    return;
}
if (args.Length == 1 && args[0] == "--gpu-test-parent")
{
    using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--gpu-test-child")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    })!;
    Console.WriteLine(child.Id);
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return;
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    checks++;
}

var acDcSetting = new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
foreach (int version in new[] { 0, 1, 8191, 8192, 8193, int.MaxValue })
    Check(ItsModePolicy.UsesLegacyService(version) == (version < 8192), "ITS service selection follows LLT's dispatcher version boundary, not model year");
Check(ItsModePolicy.ServiceCommands(true, ItsMode.Intelligent).SequenceEqual(new[] { 135 }), "Legacy intelligent mode enables automatic cooling");
Check(ItsModePolicy.ServiceCommands(true, ItsMode.Cool).SequenceEqual(new[] { 134, 146 }), "Legacy quiet mode disables automatic cooling before selecting quiet");
Check(ItsModePolicy.ServiceCommands(true, ItsMode.Performance).SequenceEqual(new[] { 134, 148 }), "Legacy performance mode disables automatic cooling before selecting performance");
Check(ItsModePolicy.ServiceCommands(true, ItsMode.Creator).SequenceEqual(new[] { 134, 148 }), "Legacy Geek first selects performance via LITSSVC");
Check(ItsModePolicy.ServiceCommands(false, ItsMode.Intelligent).SequenceEqual(new[] { 163 }) &&
    ItsModePolicy.ServiceCommands(false, ItsMode.Cool).SequenceEqual(new[] { 164 }) &&
    ItsModePolicy.ServiceCommands(false, ItsMode.Performance).SequenceEqual(new[] { 165 }) &&
    ItsModePolicy.ServiceCommands(false, ItsMode.Creator).SequenceEqual(new[] { 172 }), "Modern ITS service commands remain unchanged");
foreach (int automatic in new[] { -1, 0, 1, 2, 3 })
foreach (int current in new[] { -1, 0, 1, 2, 3, 4 })
foreach (bool geek in new[] { false, true })
{
    ItsMode? expected = (automatic, current) switch
    {
        (2, 0) => ItsMode.Intelligent,
        (1, 1) => ItsMode.Cool,
        (1, 3) => geek ? ItsMode.Creator : ItsMode.Performance,
        _ => null
    };
    Check(ItsModePolicy.FromLegacySettings(automatic, current, geek) == expected, "Legacy ITS mode requires matching automatic/current fields and never guesses unknown combinations");
}
Check(ItsModePolicy.FromCurrentSetting(0) == ItsMode.Intelligent && ItsModePolicy.FromCurrentSetting(1) == ItsMode.Cool &&
    ItsModePolicy.FromCurrentSetting(3) == ItsMode.Performance && ItsModePolicy.FromCurrentSetting(4) == ItsMode.Creator &&
    ItsModePolicy.FromCurrentSetting(2) is null, "Modern ITS state mapping remains unchanged");
foreach (bool querySucceeded in new[] { false, true })
foreach (uint capability in new uint[] { 0, 1, 0x20000, 0x20001, 0x60001 })
    Check(ItsCapabilityPolicy.SupportsLegacyFourthMode(querySucceeded, capability) ==
        (querySucceeded && (capability & 0x20001) == 0x20001), "Legacy Geek capability uses LLT query 10 bits without modern DWORD requirement");
Check(ItsModePolicy.LegacyGeekDisabled == 0x000F100B && ItsModePolicy.LegacyGeekEnabled == 0x001F100B, "Legacy Geek commands match LLT's enable/disable values");
string StartupXml(string command, string enabled = "true")
{
    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    return new XElement(ns + "Task",
        new XElement(ns + "Settings", new XElement(ns + "Enabled", enabled)),
        new XElement(ns + "Actions", new XElement(ns + "Exec",
            new XElement(ns + "Command", command), new XElement(ns + "Arguments", "--startup")))).ToString();
}
const string startupPath = @"C:\Data\YogaITS\YogaITS.Tray.exe";
var startupDefinition = XDocument.Parse(StartupTaskPolicy.CreateDefinition("Yoga ITS Tray", startupPath, "S-1-5-21-1-2-3-1001"));
XNamespace startupNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
XElement startupSettings = startupDefinition.Root!.Element(startupNs + "Settings")!;
foreach (string condition in new[] { "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "RunOnlyIfIdle", "RunOnlyIfNetworkAvailable" })
    Check((string?)startupSettings.Element(startupNs + condition) == "false", "Startup XML explicitly disables " + condition);
Check((string?)startupSettings.Element(startupNs + "ExecutionTimeLimit") == "PT0S", "Startup task has no execution time limit");
Check((string?)startupSettings.Element(startupNs + "IdleSettings")?.Element(startupNs + "StopOnIdleEnd") == "false", "Startup task never stops when idle ends");
Check(startupDefinition.Descendants(startupNs + "ExecutionTimeLimit").Count() == 2 &&
    startupDefinition.Descendants(startupNs + "ExecutionTimeLimit").All(element => element.Value == "PT0S"), "Task and logon trigger both have unlimited execution");
Check(startupDefinition.Descendants(startupNs + "LogonType").Single().Value == "InteractiveToken" &&
    startupDefinition.Descendants(startupNs + "RunLevel").Single().Value == "HighestAvailable", "Startup XML retains interactive elevated logon");
Check(StartupTaskPolicy.IsEnabledForExecutable(startupDefinition.ToString(), startupPath), "Generated task definition matches current executable");
foreach (string command in new[] { startupPath, '"' + startupPath + '"', @"c:\data\yogaits\YOGAITS.TRAY.EXE", @"C:\Data\YogaITS\..\YogaITS\YogaITS.Tray.exe" })
    Check(StartupTaskPolicy.IsEnabledForExecutable(StartupXml(command), startupPath), "Startup task matches current executable after path normalization");
Check(!StartupTaskPolicy.IsEnabledForExecutable(StartupXml(@"D:\old\YogaITS.Tray.exe"), startupPath), "Startup task for another installation is off");
foreach (string disabled in new[] { "false", "0", "invalid" })
    Check(!StartupTaskPolicy.IsEnabledForExecutable(StartupXml(startupPath, disabled), startupPath), "Disabled or invalid task setting is off");
foreach (string command in new[] { "", "YogaITS.Tray.exe", "cmd.exe", startupPath + " --startup" })
    Check(!StartupTaskPolicy.IsEnabledForExecutable(StartupXml(command), startupPath), "Missing, relative, or combined command does not match current executable");
foreach (string xml in new[] { "", "<broken>", "<Task/>", "<!DOCTYPE Task [<!ENTITY x 'test'>]><Task/>" })
    Check(!StartupTaskPolicy.IsEnabledForExecutable(xml, startupPath), "Malformed or unexpected task XML fails closed");
Check(!StartupTaskPolicy.IsEnabledForExecutable(StartupXml(startupPath), null), "Unknown current executable cannot report startup enabled");
const string unicodePath = @"C:\测试目录\YogaITS\YogaITS.Tray.exe";
Check(StartupTaskPolicy.IsEnabledForExecutable(StartupXml(unicodePath), unicodePath), "Unicode executable paths match");
var multiActionTask = XDocument.Parse(StartupXml(startupPath));
XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
multiActionTask.Root!.Element(taskNs + "Actions")!.Add(new XElement(taskNs + "Exec", new XElement(taskNs + "Command", @"D:\old\YogaITS.Tray.exe")));
Check(!StartupTaskPolicy.IsEnabledForExecutable(multiActionTask.ToString(), startupPath), "Mixed executable actions do not count as this app's startup task");
Check(AppGpuPreference.PowerSavingDefault(null) == "GpuPreference=1;", "Unconfigured app defaults to power-saving GPU");
Check(AppGpuPreference.PowerSavingDefault("") == "GpuPreference=1;", "Empty GPU configuration defaults to power saving");
foreach (string preference in new[] { "GpuPreference=0;", "GpuPreference=1;", "GpuPreference=2;", "AutoHDREnable=1;gpupreference=2;", " GpuPreference =2;" })
    Check(AppGpuPreference.PowerSavingDefault(preference) == preference, "Explicit user GPU choices remain unchanged");
Check(AppGpuPreference.PowerSavingDefault("AutoHDREnable=1;") == "AutoHDREnable=1;GpuPreference=1;", "Default GPU preference preserves other graphics options");
Check(AppGpuPreference.PowerSavingDefault("VRROptimizeEnable=0") == "VRROptimizeEnable=0;GpuPreference=1;", "Default GPU preference handles missing separator");
Check(AppGpuPreference.PowerSavingDefault("NotGpuPreference=2;") == "NotGpuPreference=2;GpuPreference=1;", "GPU option matching is exact");
Check(AppGpuPreference.HighPerformancePreference(null) == "GpuPreference=2;", "Keep-awake helper defaults to high-performance GPU");
Check(AppGpuPreference.HighPerformancePreference("GpuPreference=1;AutoHDREnable=1;") == "AutoHDREnable=1;GpuPreference=2;", "Only the helper replaces a conflicting GPU preference and preserves other options");
Check(GpuKeepAwakePolicy.CheckInterval == TimeSpan.FromMinutes(10), "Keep-awake health and parent checks use ten-minute intervals");
foreach (bool present in new[] { false, true })
foreach (bool supported in new[] { false, true })
foreach (int effective in new[] { -1, 0, 1 })
    Check(GpuKeepAwakePolicy.ShowGpuControls(present, supported, effective) ==
        (present && (!supported || effective != 1)), "GPU controls hide in integrated-only mode even before NVIDIA disappears; persisted keep-awake does not override visibility");
foreach (uint vendor in new uint[] { 0x10DE, 0x8086, 0x1002, 0x1414, 0 })
foreach (uint flags in new uint[] { 0, 1, 2, 3 })
    Check(GpuKeepAwakePolicy.IsNvidiaHardware(vendor, flags) == (vendor == 0x10DE && (flags & 2) == 0), "Explicit NVIDIA vendor selection never chooses Intel/AMD/software adapters");
foreach (bool enabled in new[] { false, true })
foreach (bool present in new[] { false, true })
foreach (bool supported in new[] { false, true })
foreach (int effective in new[] { -1, 0, 1 })
foreach (bool suspended in new[] { false, true })
foreach (bool displayOn in new[] { false, true })
foreach (int power in new[] { -1, 0, 1 })
    Check(GpuKeepAwakePolicy.MayRun(enabled, present, supported, effective, power, suspended, displayOn) ==
        (enabled && present && power == 0 && !suspended && displayOn && (!supported || effective == 0)), "All modes stop on battery, unknown power, suspend, screen-off, unknown/conflicting mode or IGPU-only without clearing the preference");
var sessionDisplayStatus = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
Check(SystemEventMonitor.ReadDisplayState(sessionDisplayStatus, 4, 0) == false, "Screen-off pauses keep-awake independently of suspend");
foreach (uint state in new uint[] { 1, 2 })
    Check(SystemEventMonitor.ReadDisplayState(sessionDisplayStatus, 4, state) == true, "On and dimmed displays permit re-evaluation, not unconditional helper startup");
foreach (uint state in new uint[] { 3, uint.MaxValue })
    Check(SystemEventMonitor.ReadDisplayState(sessionDisplayStatus, 4, state) is null, "Unknown display states do not overwrite the last confirmed state");
foreach (uint length in new uint[] { 0, 1, 3, 5, uint.MaxValue })
    Check(SystemEventMonitor.ReadDisplayState(sessionDisplayStatus, length, 0) is null, "Malformed display payloads are ignored");
Check(SystemEventMonitor.ReadDisplayState(null, 4, 0) is null &&
    SystemEventMonitor.ReadDisplayState(Guid.Empty, 4, 0) is null, "Unrelated power notifications cannot change screen state");
Check(SystemEventMonitor.ClassifyMessage(0x0218, 0x8013, sessionDisplayStatus) == SystemStateChange.None,
    "Screen power notifications do not trigger topology, WMI or color refreshes");
Check(SystemEventMonitor.ReadSleepState(0x0218, 4) == true, "Suspend is forwarded to the main keep-awake controller");
foreach (nint resume in new nint[] { 7, 0x12 })
    Check(SystemEventMonitor.ReadSleepState(0x0218, resume) == false, "Resume permits the main controller to re-evaluate keep-awake");
Check(SystemEventMonitor.ReadSleepState(0x0218, 0xA) is null && SystemEventMonitor.ReadSleepState(0x0007, 4) is null,
    "Power-source and focus changes do not overwrite suspend state");
foreach (bool exists in new[] { false, true })
foreach (int removed in new[] { 0, unchecked((int)0x887A0005), unchecked((int)0x887A0006), unchecked((int)0x887A0007) })
    Check(GpuKeepAwakePolicy.IsDeviceUsable(exists, removed) == (exists && removed >= 0), "Device reset/removal/hang invalidates the retained context for recreation");
Check(GpuKeepAwakePolicy.ParentMatches(123, 456, 123, 456, "YogaITS.Tray", false), "Keep-awake owner matches PID and start time");
Check(!GpuKeepAwakePolicy.ParentMatches(123, 456, 123, 457, "YogaITS.Tray", false), "Reused parent PID is rejected");
Check(!GpuKeepAwakePolicy.ParentMatches(123, 456, 123, 456, "YogaITS.Tray", true), "Exited parent is rejected");
Check(!GpuKeepAwakePolicy.ParentMatches(123, 456, 123, 456, "other", false), "Foreign parent is rejected before creating a GPU context");
var gpuOccupants = new[]
{
    new GpuProcessInfo(int.MaxValue, "nvcontainer.exe"),
    new GpuProcessInfo(int.MaxValue - 1, "svchost.exe"),
    new GpuProcessInfo(int.MaxValue - 2, "dwm.exe"),
    new GpuProcessInfo(int.MaxValue - 3, "explorer.exe"),
    new GpuProcessInfo(int.MaxValue - 4, "DWM.EXE"),
    new GpuProcessInfo(int.MaxValue - 5, "EXPLORER.EXE"),
    new GpuProcessInfo(int.MaxValue - 6, "YogaITS.dGPUKeepAwake.exe"),
    new GpuProcessInfo(int.MaxValue - 7, "YOGAITS.DGPUKEEPAWAKE"),
    new GpuProcessInfo(int.MaxValue - 8, @"C:\Data\YogaITS\YogaITS.dGPUKeepAwake.exe"),
    new GpuProcessInfo(int.MaxValue - 9, "alias", @"C:\Data\YogaITS\YogaITS.dGPUKeepAwake.exe"),
    new GpuProcessInfo(int.MaxValue, "duplicate"),
    new GpuProcessInfo(Environment.ProcessId, "YogaITS.Tray.exe"),
    new GpuProcessInfo(0, "idle"),
    new GpuProcessInfo(-1, "invalid")
};
var gpuTargets = NvidiaGpuHelper.GetKillCandidates(gpuOccupants);
Check(gpuTargets.Select(app => app.Pid).SequenceEqual(new[] { int.MaxValue, int.MaxValue - 1 }),
    "LLT batch GPU filtering excludes DWM/Explorer case-insensitively, retains driver/system names without owner/session checks, and deduplicates PIDs");
Check(!gpuTargets.Any(app => app.Pid <= 0 || app.Pid == Environment.ProcessId), "GPU batch never targets invalid/self PIDs");
Check(gpuTargets.Any(app => app.Name == "nvcontainer.exe"), "LLT batch still permits NVIDIA container termination");
Check(NvidiaGpuHelper.GetKillCandidates(Array.Empty<GpuProcessInfo>()).Count == 0,
    "Empty GPU occupant list has no termination targets");
Check(SettingsOptionText.GroupedName("性能选项", "性能模式") == "性能选项中的性能模式", "Narrator name includes performance group");
foreach (bool supported in new[] { false, true })
foreach (bool preference in new[] { false, true })
foreach (bool limited in new[] { false, true })
foreach (int active in new[] { 0, 1, 2 })
{
    var state = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        value = (supported ? 1u << 6 : 0) | (preference ? 1u << 7 : 0) | (limited ? 1u << 3 : 0),
        activeColorMode = active
    };
    Check(ColorBackendPolicy.CanToggleAcm(state.WideColorSupported, state.WideColorUserEnabled) == (supported || preference),
        "ACM remains operable independently of actual HDR/WCG mode and power-policy restrictions");
}
foreach (int ability in new[] { 0, 8, 0x100, 0x200, 0x400, 0x108, 0x208, 0x408, 0x308, 0x708 })
foreach (byte mode in new byte[] { 0, 4, 6, 8, 255 })
{
    int bit = mode switch { 4 => 0x100, 6 => 0x200, 8 => 0x400, _ => 0 };
    Check(YogaITS.Tray.Services.Color.LenovoTcon.LenovoManagerPath.SupportsMode(ability, mode) == ((ability & 8) != 0 && bit != 0 && (ability & bit) != 0),
        "TCON requires gamut and target-specific capability bits; unknown modes fail closed");
}
foreach (bool tcon in new[] { false, true })
foreach (bool icc in new[] { false, true })
foreach (bool absent in new[] { false, true })
foreach (bool eligible in new[] { false, true })
    Check(ColorBackendPolicy.Select(tcon, icc, absent, eligible) == (tcon ? ColorGamutBackend.Tcon :
        eligible && (icc || absent) ? ColorGamutBackend.Icc : ColorGamutBackend.None),
        "Live TCON takes priority, validated ICC remains available, uncertain TCON never offers blind ICC recovery");
foreach (byte raw in new byte[] { 4, 6, 8 })
    Check(ColorBackendPolicy.ModeRaw(ColorBackendPolicy.ModeId(raw)) == raw, "TCON IDs roundtrip without ICC ambiguity");
Check(ColorBackendPolicy.ModeId(255) == "未知", "Unknown TCON raw value remains unknown");
Check(SettingsOptionText.ColorLabel("TconP3") == "P3" && SettingsOptionText.ColorLabel("TconSrgb") == "sRGB" &&
    SettingsOptionText.ColorLabel("TconAdobeRgb") == "Adobe RGB", "TCON labels do not imply ICC white points or brightness");
Check(SettingsOptionText.VoiceRegistrationStatus("正在录制声纹", true, 12) == "正在录音，剩余 12 秒。请连续朗读下方文字。", "Panel refresh preserves the current recording countdown");
Check(SettingsOptionText.VoiceRegistrationStatus("正在注册并验证声纹……", false, 12) == "正在注册并验证声纹……", "Registration phase does not retain an old recording countdown");
Check(SettingsOptionText.VoiceRegistrationStatus("正在录制声纹", true, 0) == "正在录制声纹", "Recording initially uses the service status until progress arrives");
Check(SettingsOptionText.FlipToStartDescription(false) == "开启后，当您打开笔记本盖子时，系统将自动开机。", "Lid-open description initially omits restart advice");
Check(SettingsOptionText.FlipToStartDescription(true).EndsWith("（设置改变后，重启电脑方可生效）"), "Lid-open operation adds restart advice");
Check(SettingsOptionText.GroupedName("充电选项", "养护模式") == "充电选项中的养护模式", "Narrator name distinguishes charging group");
Check(SettingsOptionText.GroupedName("独显休眠", "结束独显\r\n占用进程") == "独显休眠中的结束独显占用进程", "Narrator names remove visual line breaks");
Check(SystemEventMonitor.ClassifyMessage(0x001A, 0) == SystemStateChange.Appearance,
    "Desktop setting changes refresh appearance without triggering hardware policies");
Check(SystemEventMonitor.ClassifyMessage(0x031A, 0) == SystemStateChange.Appearance,
    "Desktop theme changes refresh appearance without a CoreWindow event subscription");
Check(WindowSizing.Fit(500, 96, 1920) == 500, "Window width uses DIP at 100 percent DPI");
Check(WindowSizing.Fit(500, 192, 1920) == 1000, "Window width scales at 200 percent DPI");
Check(WindowSizing.Fit(500, 0, 1920) == 500, "Unavailable DPI defaults to 96");
Check(WindowSizing.Fit(500, 192, 800) == 752, "Window fits work area with a scaled margin");
Check(WindowSizing.Fit(280, 144, 1000) == 420, "Window height scales at 150 percent DPI");
Check(WindowSizing.Fit(280, 192, 10) == 1, "Tiny work area never produces a negative size");
foreach (int source in new[] { -1, 0, 1, 255 })
    Check(ItsCapabilityPolicy.CanSelectFourthMode(source) == (source == 0),
        "Fourth ITS mode requires confirmed AC power; battery and unknown stay disabled");
foreach (bool success in new[] { false, true })
foreach (uint current in new uint[] { 0, 1, 0x2300, 0x2301 })
    Check(ItsCapabilityPolicy.SupportsIts(success, current) == (success && (current & 1) != 0),
        "ITS requires a successful current-mode query and the command-valid bit");
foreach (bool success in new[] { false, true })
foreach (uint capability in new uint[] { 0, 1, 0x20000, 0x20001, 0x60001 })
foreach (object? state in new object?[] { null, "4", new byte[] { 4 }, 4L, 0, 1, 3, 4 })
    Check(ItsCapabilityPolicy.SupportsFourthMode(success, capability, state) ==
        (success && (capability & 0x20001) == 0x20001 && state is int),
        "Fourth ITS mode requires both advertised bits and a readable versioned DWORD, not active mode 4");
Check(SystemEventMonitor.ClassifyMessage(0x0218, 0x8013, acDcSetting) == SystemStateChange.Power,
    "AC/DC notifications refresh GPU state without a visible or active window");
Check(SystemEventMonitor.ClassifyMessage(0x0218, 0x8013, Guid.Empty) == SystemStateChange.None,
    "Unrelated power settings do not trigger GPU policy");
Check(SystemEventMonitor.ClassifyMessage(0x0218, 0x8013) == SystemStateChange.None,
    "Missing power setting data is ignored");
Check(SystemEventMonitor.ClassifyMessage(0x0218, 0x000A) == SystemStateChange.Power,
    "General power status changes refresh cached GPU state");
foreach (int resume in new[] { 0x0012, 0x0007 })
    Check(SystemEventMonitor.ClassifyMessage(0x0218, resume) == (SystemStateChange.Power | SystemStateChange.Display),
        "Resume updates power and display state");
Check(SystemEventMonitor.ClassifyMessage(0x007E, 0) == SystemStateChange.Display,
    "Display topology notifications refresh GPU state");
Check(SystemEventMonitor.ClassifyMessage(0x0219, 0x0007) == SystemStateChange.Display,
    "Monitor or dock device changes refresh even without a resolution change");
Check(SystemEventMonitor.ClassifyMessage(0x0219, 0x8001) == SystemStateChange.None,
    "Device removal permission queries do not trigger hardware policy");
Check(SystemEventMonitor.ClassifyMessage(0x0007, 0) == SystemStateChange.None &&
    SystemEventMonitor.ClassifyMessage(0x0008, 0) == SystemStateChange.None,
    "Focus gain/loss are not required system event sources");

foreach (int power in new[] { -1, 0, 1, 2 })
foreach (var displayState in Enum.GetValues<DgpuDisplayState>())
{
    int? expected = power == 0 ? 0 : power == 1 && displayState == DgpuDisplayState.Disconnected ? 1 : null;
    Check(GpuAutoPolicy.Target(power, displayState) == expected, $"GPU power={power}, display={displayState}");
}

HpdState Hpd(int aware, int sensor) => new(aware, sensor, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0);
foreach (int aware in new[] { 0, 1 })
foreach (int sensor in new[] { 2, 3, 4 })
foreach (long capability in new[] { 0L, 0x40L })
{
    var features = HpdFeatureProfile.Create(capability, Hpd(aware, sensor), 0);
    Check(features.WakeSleepSection == (aware != 0), "HPD aware gates wake/sleep");
    Check(features.CameraAssist == (aware != 0 && sensor != 4 && capability == 0x40), "HPD camera assist bit and sensor");
    Check(features.AutoVideoPause == (aware != 0 || sensor is 3 or 4), "HPD F4009 fallback");
}
var failed = HpdFeatureProfile.Create(0x40, Hpd(1, 3), -1);
Check(!failed.WakeSleepSection && !failed.AutoVideoPause && !failed.CameraAssist, "Failed HPD read hides features");

Check(AppLaunchPolicy.ShouldOpenControlCenter([]), "Argument-free launch opens the control center");
int iconIndex = Array.IndexOf(args, "--icon-executable");
if (iconIndex >= 0)
{
    foreach (bool small in new[] { false, true })
    {
        using var icon = ApplicationIcon.Load(small, args[iconIndex + 1]);
        Check(!icon.IsInvalid && !icon.IsClosed, "Published executable supplies a valid embedded icon handle");
        icon.Dispose();
        Check(icon.IsClosed, "Extracted icon ownership is released safely");
    }
}
foreach (string[] arguments in new[] { new[] { "--startup" }, new[] { "--STARTUP" }, new[] { "other", "--startup" } })
    Check(!AppLaunchPolicy.ShouldOpenControlCenter(arguments), "Startup launch never opens or signals the control center");
foreach (string[] arguments in new[] { new[] { "--startup-other" }, new[] { "--startup=true" }, new[] { "other" } })
    Check(AppLaunchPolicy.ShouldOpenControlCenter(arguments), "Only the exact startup flag suppresses the control center");

var display = new DisplayInfo(default, 0, 0, 11, "test", "test", "", "", "test", "PANEL", 1, null, 0);
Check(display.IsBlocked, "Unknown Advanced Color blocks writes");
foreach (uint value in new[] { 0u, 1u << 5, 1u << 7, (1u << 5) | (1u << 7), (1u << 3) | (1u << 5) })
foreach (int mode in new[] { 0, 1, 2, 99 })
{
    var advanced = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 { value = value, activeColorMode = mode };
    var actualDisplay = display with { AdvancedColor = advanced };
    Check(actualDisplay.IsBlocked == (mode != 0), "Only effective SDR permits gamut, regardless of preferences/policy");
    Check(actualDisplay.HdrEnabled == (mode == 2) && advanced.HdrActive == (mode == 2), "HDR display state uses actual mode");
    Check(actualDisplay.AcmEnabled == (mode == 1) && advanced.AcmActive == (mode == 1), "ACM display state uses actual mode");
    Check(advanced.WideColorUserEnabled == ((value & (1u << 7)) != 0),
        "ACM switch setting remains independent of effective SDR/WCG/HDR mode");
    Check((actualDisplay.ColorBlockReason.Length > 0) == (mode != 0), "Block reason follows effective mode");
}
var batteryColor = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
{
    value = (1u << 3) | (1u << 4) | (1u << 5), activeColorMode = 0
};
Check(batteryColor.HighDynamicRangeUserEnabled && !batteryColor.HdrActive,
    "Battery policy can suspend HDR without clearing user preference");
Check(SettingsOptionText.ColorStatus("Native", "", batteryColor.HdrActive, batteryColor.AcmActive) ==
    SettingsOptionText.ColorStatus("Native", "", false, false), "Suspended HDR shows SDR color status");
var pluggedColor = batteryColor;
pluggedColor.activeColorMode = 2;
Check(pluggedColor.HighDynamicRangeUserEnabled == batteryColor.HighDynamicRangeUserEnabled && pluggedColor.HdrActive &&
    (display with { AdvancedColor = pluggedColor }).IsBlocked, "Restored HDR blocks gamut without changing preference");
var supportedColor = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 { value = (1u << 4) | (1u << 6) };
Check(supportedColor.HighDynamicRangeSupported && supportedColor.WideColorSupported, "HDR and ACM support bits");
Check(!supportedColor.HighDynamicRangeUserEnabled && !supportedColor.WideColorUserEnabled, "Support is not enabled state");
Check(!(display with { AdvancedColor = supportedColor }).IsBlocked, "Supported advanced color does not block SDR");
Check(new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 { value = 1u << 3 }.LimitedByPolicy, "Advanced color policy flag");
var targetDisplay = display with { AdapterId = new NativeMethods.LUID { LowPart = 42, HighPart = 7 }, SourceId = 3, TargetId = 9 };
foreach (bool hdr in new[] { false, true })
foreach (bool enabled in new[] { false, true })
{
    var request = DisplayService.CreateColorStateRequest(targetDisplay, hdr, enabled);
    Check(request.header.type == (hdr ? 16 : 17), "HDR/ACM setter request type");
    Check(request.header.size == 24, "Native color setter is 24 bytes");
    Check(request.header.id == 9 && request.header.adapterId.LowPart == 42 && request.header.adapterId.HighPart == 7, "Setter addresses target, not source");
    Check(request.value == (enabled ? 1u : 0u), "Setter enable bit");
}

foreach (var (preparedAtStart, resetResult, unprepareResult, closeResult, expectedCalls) in new[]
{
    (true, 0u, 0u, 0u, "reset,unprepare,close"),
    (false, 0u, 0u, 0u, "reset,close"),
    (true, 7u, 0u, 0u, "reset"),
    (true, 0u, 33u, 0u, "reset,unprepare"),
    (true, 0u, 0u, 33u, "reset,unprepare,close")
})
{
    var calls = new List<string>();
    bool prepared = preparedAtStart;
    uint cleanupResult = VoiceRegistrationRecorder.ReleaseDevice(ref prepared,
        () => { calls.Add("reset"); return resetResult; },
        () => { calls.Add("unprepare"); return unprepareResult; },
        () => { calls.Add("close"); return closeResult; });
    Check(string.Join(",", calls) == expectedCalls, "Capture cleanup resets before unpreparing/closing and stops on failure");
    Check((cleanupResult == 0) == (resetResult == 0 && unprepareResult == 0 && closeResult == 0),
        "Capture cleanup permits freeing native memory only after successful release");
    Check(prepared == (preparedAtStart && (resetResult != 0 || unprepareResult != 0)),
        "Failed unprepare retains ownership for safe cleanup retry");
}
using (var stoppedCapture = new CancellationTokenSource())
using (var recorder = new VoiceRegistrationRecorder())
{
    stoppedCapture.Cancel();
    bool canceled = false;
    try { await recorder.RecordAsync("unused.wav", TimeSpan.FromSeconds(20), cancellationToken: stoppedCapture.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "Pre-canceled voice capture never opens a microphone or creates a recording file");
}

string Voice(double[] data, int count = 192, string type = "FLOAT32") => JsonSerializer.Serialize(new
{
    Header = new { status = "OK" },
    Content = new { voiceid = new { metadata = new { data_count = count, data_type = type }, data } }
});
double[] unit = new double[192];
unit[0] = 1;
Check(ElevocJson.SpeakerId(Voice(unit)).IsValid, "Valid normalized 192-dimensional voiceprint");
Check(!ElevocJson.SpeakerId(Voice(new double[192])).IsValid, "Zero-filled voiceprint rejected");
Check(!ElevocJson.SpeakerId(Voice([1.0])).IsValid, "Truncated voiceprint rejected");
Check(!ElevocJson.SpeakerId(Voice(unit, 191)).IsValid, "Declared voiceprint size mismatch rejected");
Check(!ElevocJson.SpeakerId(Voice(unit, type: "FLOAT64")).IsValid, "Wrong voiceprint type rejected");
Check(!ElevocJson.SpeakerId("{\"Content\":{\"voiceid\":{\"metadata\":{}}}}").IsValid, "Private-mode metadata does not verify registration");
Check(ElevocJson.SupportedFunctions("{bad json").Count == 0, "Unknown audio capability fails closed");
Check(ElevocJson.Mode("{\"Content\":{\"mode\":\"environmental\"},\"Header\":{\"status\":\"OK\"}}") == "environmental",
    "Microphone noise cancellation off reads back as environmental");
Check(ElevocJson.Mode("{\"Content\":{\"mode\":\"private\"},\"Header\":{\"status\":\"OK\"}}") != "environmental",
    "Private mode is not mistaken for noise cancellation off");

foreach (bool internalMic in new[] { false, true })
    Check(ElevocMicrophonePolicy.SupportsMultiplePeople(internalMic) == internalMic,
        "Multi-person mode requires an internal microphone, not uplink capability alone");
foreach (bool internalMic in new[] { false, true })
foreach (bool supported in new[] { false, true })
foreach (string? mode in new string?[] { null, "environmental", "private", "voiceid", "shared", "special", "unknown" })
{
    Check(ElevocMicrophonePolicy.ShowVoice(internalMic, supported, mode) ==
        (internalMic && supported && mode is "private" or "voiceid"), "Voice option requires internal mic and single-person family");
    Check(ElevocMicrophonePolicy.ShowPosition(internalMic, supported, mode) ==
        (internalMic && supported && mode is "shared" or "special"), "Position option requires internal mic and multi-person family");
}
var arrayMic = new AudioEndpointInfo(AudioDataFlow.Capture, "test", "renamed device",
    new Guid("DFF21BE5-F70F-11D0-B917-00A0C9223196"));
var propVariantType = typeof(CoreAudioEndpointMonitor).GetNestedType("PropVariant",
    System.Reflection.BindingFlags.NonPublic)!;
Check(System.Runtime.InteropServices.Marshal.SizeOf(propVariantType) == 24,
    "Core Audio PROPVARIANT reserves the full x64 native union");
Check(System.Runtime.InteropServices.Marshal.OffsetOf(propVariantType, "VarType").ToInt32() == 0 &&
    System.Runtime.InteropServices.Marshal.OffsetOf(propVariantType, "PointerValue").ToInt32() == 8,
    "Core Audio PROPVARIANT header and pointer match native offsets");
Check(arrayMic.IsInternalMicrophone, "Array microphone classification does not depend on friendly name");
Check(!(arrayMic with { Flow = AudioDataFlow.Render }).IsInternalMicrophone, "Render endpoint cannot be an internal microphone");
Check(!(arrayMic with { JackSubType = null }).IsInternalMicrophone, "Unknown microphone type hides sub-options");
Check(!(arrayMic with { JackSubType = new Guid("DFF21BE3-F70F-11D0-B917-00A0C9223196") }).IsInternalMicrophone,
    "Headset microphone does not expose internal directional sub-options");
var headsetMic = arrayMic with { JackSubType = new Guid("DFF21BE3-F70F-11D0-B917-00A0C9223196") };
Check(!ElevocMicrophonePolicy.SupportsMultiplePeople(headsetMic.IsInternalMicrophone),
    "Headset hides multi-person mode and rejects shared writes");
Check(ElevocMicrophonePolicy.SupportsMultiplePeople(arrayMic.IsInternalMicrophone),
    "Returning to the array microphone restores multi-person availability");
Check(!ElevocMicrophonePolicy.SupportsMultiplePeople((arrayMic with { JackSubType = null }).IsInternalMicrophone),
    "Unknown microphone type conservatively hides multi-person mode");
foreach (var (mode, label) in new[]
{
    ("Default", "出厂默认（D65）"), ("Native", "鲜艳（Native）"), ("sRGB", "网页办公（sRGB-D65）"),
    ("DisplayP3", "摄影/数码影院（P3-D65）"), ("DCIP3", "原生数码影院（DCI-P3）"),
    ("AdobeRGB", "摄影印刷（Adobe-RGB-D65）"), ("REC709", "SDTV视频（REC709）")
})
{
    Check(SettingsOptionText.ColorLabel(mode) == label, "Color display labels preserve profile IDs");
    Check(SettingsOptionText.ColorLabel(mode, 80) == label + "[80]", "Color label includes factory brightness");
}
Check(SettingsOptionText.ColorLabel("Custom") == "Custom", "Unknown color profiles keep original names");
foreach (var (hdr, acm) in new[] { (true, false), (false, true), (true, true) })
    Check(SettingsOptionText.ColorStatus("Native", "旧提示", hdr, acm) ==
        "当前颜色配置：不适用。HDR或者ACM已开启。", "Advanced color status replaces inactive ICC name and old hint");
Check(SettingsOptionText.ColorStatus("Native", "校色文件状态", false, false) ==
    "当前颜色配置：鲜艳（Native）。校色文件状态", "SDR status retains profile name and relevant messages");
Check(SettingsOptionText.MicrophoneDescription("environmental").Contains("所有"), "Off microphone description");
Check(SettingsOptionText.MicrophoneDescription("voiceid").Contains("学习"), "Voice ID microphone description");
Check(SettingsOptionText.MicrophoneDescription("special").Contains("空间"), "Position microphone description");
Check(SettingsOptionText.MicrophoneDescription(null) == "", "Unknown modes do not show an unrelated description");
Check(SettingsOptionText.ChargeDescription("Conservation", false).Contains("75%-80%"), "Conservation charge description");
Check(SettingsOptionText.ChargeDescription("Normal", false).Contains("常规"), "Normal charge description");
Check(SettingsOptionText.ChargeDescription("RapidCharge", false).Contains("更快"), "Rapid charge description");
Check(!SettingsOptionText.ChargeDescription("Normal", true).Contains("常规") &&
    SettingsOptionText.ChargeDescription("Normal", true).Contains("80%"), "Night selection displays its own mutually exclusive description");

foreach (var requested in Enum.GetValues<BatteryChargeMode>())
foreach (bool night in new[] { false, true })
foreach (bool supported in new[] { false, true })
{
    var calls = new List<string>();
    var charge = new BatteryChargeSnapshot(true, BatteryChargeMode.Conservation, "test");
    var nightState = new BatteryToggleSnapshot(supported, supported);
    var result = await BatteryChargeSelection.ApplyAsync(requested, night, () => nightState, () => charge,
        enabled =>
        {
            calls.Add("night:" + enabled);
            nightState = new(supported, enabled);
            return Task.FromResult(new BatteryToggleResult(true, "", nightState));
        },
        mode =>
        {
            calls.Add("mode:" + mode);
            charge = new(true, mode, "test");
            return Task.FromResult(new BatteryActionResult(true, "", charge));
        });
    var expectedMode = night ? BatteryChargeMode.Normal : requested;
    string[] expected = night && !supported ? [] : supported
        ? ["night:" + night, "mode:" + expectedMode] : ["mode:" + expectedMode];
    Check(calls.SequenceEqual(expected), "Night is set before base mode, and always uses Normal");
    Check(result.Success == (!night || supported), "Unsupported night selection fails without writing");
    if (result.Success)
        Check(result.Charge.Mode == expectedMode && (!supported || result.Night.Enabled == night),
            "Selection result contains both confirmed battery states");
}
bool wroteBaseMode = false;
var toggleFailure = await BatteryChargeSelection.ApplyAsync(BatteryChargeMode.Normal, true,
    () => new(true, false), () => new(true, BatteryChargeMode.Normal, "test"),
    _ => Task.FromResult(new BatteryToggleResult(false, "", new(true, false))),
    mode =>
    {
        wroteBaseMode = true;
        return Task.FromResult(new BatteryActionResult(true, "", new(true, mode, "test")));
    });
Check(!toggleFailure.Success && !wroteBaseMode, "Failed night write prevents the following base-mode write");
Check(toggleFailure.Message.Length > 0, "Failed battery operations retain a meaningful error");
var unconfirmedNight = await BatteryChargeSelection.ApplyAsync(BatteryChargeMode.Normal, true,
    () => new(true, false), () => new(true, BatteryChargeMode.Normal, "test"),
    _ => Task.FromResult(new BatteryToggleResult(true, "", new(true, true))),
    mode => Task.FromResult(new BatteryActionResult(true, "", new(true, mode, "test"))));
Check(!unconfirmedNight.Success, "Successful write acknowledgements cannot replace final night readback");
Check(!unconfirmedNight.Night.Enabled, "Failed confirmation reports actual night state");

string temporary = Path.Combine(Path.GetTempPath(), "YogaITS-PolicyChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var machine = new MachineIdentity("TEST", "SERIAL", "TEST", "");
    string native = Path.Combine(temporary, "Panel_Native.icm");
    string srgb = Path.Combine(temporary, "Panel_sRGB.icm");
    File.WriteAllBytes(native, Icc("SERIAL", "PANEL", 100));
    File.WriteAllBytes(srgb, Icc("SERIAL", "PANEL", 48));
    Check(IccInspector.ValidateFactoryDirectory(temporary, machine, "PANEL").Ok, "Factory package matches unit/panel and NUL-terminated CxF XML");
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine with { SerialNumber = "OTHER" }, "PANEL").Ok, "Other unit rejected");
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine, "OTHER").Ok, "Other panel rejected");
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine with { MachineType = "OTHER" }, "PANEL").Ok, "Other machine type rejected");
    var placeholder = new CxfIdentity(true, "INVALID", "INVALID", "PANEL", "test");
    Check(IccInspector.ValidateIdentity(placeholder, machine, "PANEL", out _), "Lenovo paired INVALID identities accept matching panel");
    Check(!IccInspector.ValidateIdentity(placeholder, machine, "OTHER", out _), "Placeholder identity still rejects another panel");
    Check(!IccInspector.ValidateIdentity(placeholder, machine, null, out _), "Placeholder identity requires a known current panel");
    Check(!IccInspector.ValidateIdentity(placeholder with { Ok = false }, machine, "PANEL", out _), "Placeholder identity cannot bypass parse failure");
    Check(!IccInspector.ValidateIdentity(placeholder with { ComputerName = "TEST" }, machine, "PANEL", out _), "Serial placeholder alone is rejected");
    Check(!IccInspector.ValidateIdentity(placeholder with { ComputerSerialNumber = "SERIAL" }, machine, "PANEL", out _), "Machine-type placeholder alone is rejected");
    Check(!IccInspector.ValidateIdentity(placeholder with { ComputerName = "", ComputerSerialNumber = "" }, machine, "PANEL", out _), "Empty identities do not qualify as Lenovo placeholders");
    Check(!IccInspector.ValidateIdentity(placeholder with { ComputerName = "UNKNOWN", ComputerSerialNumber = "UNKNOWN" }, machine, "PANEL", out _), "Other placeholders are not accepted");
    File.WriteAllBytes(native, Icc("INVALID", "PANEL", 100, "INVALID"));
    File.WriteAllBytes(srgb, Icc("INVALID", "PANEL", 48, "INVALID"));
    Check(IccInspector.ValidateFactoryDirectory(temporary, machine, "PANEL").Ok, "Factory recovery accepts Lenovo panel-only package");
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine, "OTHER").Ok, "Panel-only package cannot be used on another panel");
    File.WriteAllBytes(srgb, Icc("OTHER", "PANEL", 48));
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine, "PANEL").Ok, "Placeholder package cannot hide a different unit's profile");
    File.WriteAllBytes(native, Icc("SERIAL", "PANEL", 100));
    File.WriteAllBytes(srgb, Icc("SERIAL", "PANEL", 48));
    Check(IccMetaBrightness.Read(native).Brightness == 100 && IccMetaBrightness.Read(srgb).Brightness == 48, "Brightness comes from ICC metadata");
    File.WriteAllBytes(srgb, Icc("SERIAL", "PANEL", 101));
    Check(!IccMetaBrightness.Read(srgb).Ok, "Out-of-range brightness rejected");
    File.WriteAllBytes(srgb, Icc("OTHER", "PANEL", 48));
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine, "PANEL").Ok, "Every advertised ICC must match the unit");
    File.WriteAllBytes(srgb, [0, 1, 2]);
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine, "PANEL").Ok, "Malformed ICC rejected");
    File.Delete(native);
    Check(!IccInspector.ValidateFactoryDirectory(temporary, machine, "PANEL").Ok, "Missing Native/cache rejected");
}
finally { Directory.Delete(temporary, recursive: true); }

int factoryDirectoryIndex = Array.IndexOf(args, "--factory-directory");
if (factoryDirectoryIndex >= 0)
{
    string directory = args[factoryDirectoryIndex + 1];
    var validation = IccInspector.ValidateFactoryDirectory(directory, new("TEST", "SERIAL", "TEST", ""), "LEN8BAD");
    Check(validation.Ok && validation.ProfileCount == 7, "Real Lenovo LEN8BAD panel-only package passes full factory validation");
}

await using (var service = new FakeService())
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var continueRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task read = service.Execute(async () => { started.SetResult(); await continueRead.Task; service.Value = 1; });
    await started.Task;
    Task write = service.Execute(() => { service.Value = 2; return Task.CompletedTask; });
    continueRead.SetResult();
    await Task.WhenAll(read, write);
    Check(service.Value == 2, "Old refresh cannot overwrite a serialized newer write");
    await service.DisposeAsync();
    await service.Execute(() => { service.Value = 3; return Task.CompletedTask; });
    Check(service.Value == 2 && service.Closed, "Disposed services never invoke hardware operations");
}
if (args.Contains("--gpu-process-tree"))
{
    using Process parent = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--gpu-test-parent")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true
    })!;
    Process? child = null;
    try
    {
        string? childPid = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        child = Process.GetProcessById(int.Parse(childPid!));
        _ = child.Handle; // Keep a stable handle for verification after its parent exits.
        GpuActionResult result = await NvidiaGpuHelper.KillProcessesAsync([parent.Id]);
        Check(result.Success && parent.HasExited, "LLT termination exits the owned test parent");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.HasExited, "LLT termination also exits the owned test child");
        Console.WriteLine("PASS: process-tree smoke test terminated only its own parent/child fixtures.");
    }
    finally
    {
        if (!parent.HasExited) parent.Kill(entireProcessTree: true);
        if (child is not null)
        {
            if (!child.HasExited) child.Kill();
            child.Dispose();
        }
    }
}
int updaterIndex = Array.IndexOf(args, "--updater-executable");
checks += await UpdateChecks.RunAsync(updaterIndex >= 0 ? args[updaterIndex + 1] : null);
checks += await BuildFlavorChecks.RunAsync();
checks += await BackgroundImageChecks.RunAsync();
checks += await OptimizationChecks.RunAsync();
checks += await KeyboardBacklightChecks.RunAsync();
checks += await GpuModeChecks.RunAsync();
int gpuRestartIndex = Array.IndexOf(args, "--gpu-restart-executable");
checks += await GpuDeviceRestartChecks.RunAsync(gpuRestartIndex >= 0 ? args[gpuRestartIndex + 1] : null);
Console.WriteLine($"PASS: {checks} checks. No hardware, registry, microphone, or network operations were performed.");

static byte[] Icc(string serial, string panel, int brightness, string machineType = "TEST")
{
    string xml = new XElement("Root", new XElement("CustomAttributes",
        new XAttribute("ComputerName", machineType), new XAttribute("ComputerSerialNumber", serial),
        new XAttribute("DisplayHardwareId", panel))).ToString() + "\0";
    using var zipped = new MemoryStream();
    using (var zlib = new ZLibStream(zipped, CompressionLevel.Optimal, leaveOpen: true))
        zlib.Write(Encoding.UTF8.GetBytes(xml));
    byte[] compressed = zipped.ToArray();
    byte[] cxf = new byte[8 + compressed.Length];
    Encoding.ASCII.GetBytes("ZXML").CopyTo(cxf, 0);
    compressed.CopyTo(cxf, 8);
    byte[] key = Encoding.BigEndianUnicode.GetBytes("Brightness");
    byte[] value = Encoding.BigEndianUnicode.GetBytes(brightness.ToString());
    byte[] meta = new byte[32 + key.Length + value.Length];
    Encoding.ASCII.GetBytes("dict").CopyTo(meta, 0);
    U32(meta, 8, 1); U32(meta, 12, 16);
    U32(meta, 16, 32); U32(meta, 20, key.Length);
    U32(meta, 24, 32 + key.Length); U32(meta, 28, value.Length);
    key.CopyTo(meta, 32); value.CopyTo(meta, 32 + key.Length);
    var tags = new[] { ("CxF ", cxf), ("MHC2", new byte[4]), ("meta", meta) };
    int offset = 132 + tags.Length * 12;
    byte[] icc = new byte[offset + tags.Sum(t => t.Item2.Length)];
    U32(icc, 0, icc.Length); U32(icc, 128, tags.Length);
    for (int i = 0; i < tags.Length; i++)
    {
        int entry = 132 + i * 12;
        Encoding.ASCII.GetBytes(tags[i].Item1).CopyTo(icc, entry);
        U32(icc, entry + 4, offset); U32(icc, entry + 8, tags[i].Item2.Length);
        tags[i].Item2.CopyTo(icc, offset);
        offset += tags[i].Item2.Length;
    }
    return icc;
}

static void U32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), (uint)value);

static void CheckArchiveSize(long bytes)
{
    if (bytes <= 0 || bytes > UpdatePolicy.MaximumArchiveBytes) throw new InvalidDataException("Published archive exceeds updater limit.");
}

sealed class FakeService : SerializedFeatureService
{
    public int Value;
    public bool Closed;
    public Task Execute(Func<Task> operation) => RunAsync(operation);
    protected override void Close() => Closed = true;
}
