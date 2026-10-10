using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.Services.Hpd;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + name);
    checks++;
}

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

var display = new DisplayInfo(default, 0, 0, 11, "test", "test", "", "", "test", "PANEL", 1, null, 0);
Check(display.IsBlocked, "Unknown Advanced Color blocks writes");
foreach (var (value, mode, blocked) in new[] { (0u, 0, false), (1u << 5, 0, true), (1u << 7, 0, true), (0u, 1, true) })
{
    var advanced = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 { value = value, activeColorMode = mode };
    Check((display with { AdvancedColor = advanced }).IsBlocked == blocked, "HDR/ACM/non-SDR gate");
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
