using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.UI;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    checks++;
}

var acDcSetting = new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
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
foreach (int path in new[] { -1, 0, 1 })
{
    int? expected = power == 0 ? 0 : power == 1 && path == 0 ? 1 : null;
    Check(GpuAutoPolicy.Target(power, path) == expected, $"GPU power={power}, externalPath={path}");
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
if (args.Length == 2 && args[0] == "--icon-executable")
{
    foreach (bool small in new[] { false, true })
    {
        using var icon = ApplicationIcon.Load(small, args[1]);
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
Console.WriteLine($"PASS: {checks} checks. No hardware, registry, microphone, or network operations were performed.");

static byte[] Icc(string serial, string panel, int brightness)
{
    string xml = new XElement("Root", new XElement("CustomAttributes",
        new XAttribute("ComputerName", "TEST"), new XAttribute("ComputerSerialNumber", serial),
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

sealed class FakeService : SerializedFeatureService
{
    public int Value;
    public bool Closed;
    public Task Execute(Func<Task> operation) => RunAsync(operation);
    protected override void Close() => Closed = true;
}
