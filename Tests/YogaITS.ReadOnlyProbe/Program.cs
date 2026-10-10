using System.Security.Principal;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.Services.Hpd;

// Only RefreshAsync is invoked. In particular, GPU InitializeAsync is deliberately
// excluded because a stored Auto preference would apply hardware policy on startup.
await using var gpu = new LenovoGpuWorkingModeService();
await using var hpd = new HpdService();
await using var color = new ColorGamutService();
await using var audio = new ElevocService();
using var identity = WindowsIdentity.GetCurrent();
bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
await Task.WhenAll(gpu.RefreshAsync(), hpd.RefreshAsync(), color.RefreshAsync(), audio.RefreshAsync());
Console.WriteLine($"Read-only capability probe. Administrator={elevated}");
Console.WriteLine($"GPU: supported={gpu.Snapshot.Supported}, effective={gpu.Snapshot.EffectiveMode}, externalPath={gpu.Snapshot.ExternalPath}");
if (!gpu.Snapshot.Supported && gpu.Snapshot.Message.Length > 0)
    Console.WriteLine("GPU read diagnostic: " + gpu.Snapshot.Message);
Console.WriteLine($"HPD: wakeSleep={hpd.Snapshot.Features.WakeSleepSection}, cameraAssist={hpd.Snapshot.Features.CameraAssist}, videoPause={hpd.Snapshot.Features.AutoVideoPause}");
Console.WriteLine($"Color: supported={color.Snapshot.Supported}, blocked={color.Snapshot.Blocked}, factoryValid={color.Snapshot.FactoryValid}, modeCount={color.Snapshot.Modes.Count}");
Console.WriteLine($"Audio: uplink={audio.Snapshot.Uplink}, downlink={audio.Snapshot.Downlink}, special={audio.Snapshot.Special}, voiceId={audio.Snapshot.VoiceId}, registered={audio.Snapshot.VoiceIdRegistered}");
Console.WriteLine("No setters, recording, downloads, or system configuration writes were invoked.");
