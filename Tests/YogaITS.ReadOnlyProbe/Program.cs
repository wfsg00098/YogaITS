using System.Security.Principal;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.Services.Elevoc;
using YogaITS.Tray.Services.Hpd;
using YogaITS.Tray.Services.Color.LenovoTcon;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ProcessComSecurity.InitializeEarly();
        try { RunProbeAsync(args).GetAwaiter().GetResult(); }
        finally { ProcessComSecurity.Shutdown(); }
    }

    private static async Task RunProbeAsync(string[] args)
    {
        if (args.Contains("--its-only"))
        {
            var machine = LenovoMachineInfoHelper.Read();
            var capability = LenovoItsHelper.ReadCapabilities(machine.Model);
            var state = LenovoItsHelper.ReadMode(capability);
            Console.WriteLine($"Model: {machine.Model}; type={machine.MachineType}");
            Console.WriteLine($"ITS: supported={capability.Supported}, service={(capability.UsesLegacyService ? "LITSSVC" : "LenovoProcessManagement")}, dispatcherVersion={capability.DispatcherVersion}, fnCapability=0x{capability.ItsFnCapability:X}, geekSupported={capability.GeekModeSupported}");
            Console.WriteLine($"Mode: {state.Mode}; raw={state.RawState}; {state.DisplayName}");
            using var legacy = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LITSSVC\LNBITS\IC\MMC");
            if (legacy is not null)
                Console.WriteLine($"Legacy: AutomaticModeSetting={legacy.GetValue("AutomaticModeSetting")}, CurrentSetting={legacy.GetValue("CurrentSetting")}");
            Console.WriteLine("Read-only ITS probe: no service commands, mode writes, or Windows power-mode changes.");
            return;
        }
        if (args.Contains("--check-calibration-http"))
        {
            var machine = MachineIdentityService.Read();
            string requestId = "1S" + machine.MachineType + machine.SerialNumber;
            foreach (string accept in new[] { "application/json", "*/*", "" })
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("ClientID", "Hr0Z3kZYsCync4KzAjLqqg==");
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PC_WIN11");
                if (accept.Length > 0) http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", accept);
                using var response = await http.GetAsync("https://supportapi.lenovo.com/v3/ColorCalibration/GetFileBySn?sn=" + Uri.EscapeDataString(requestId));
                string body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Calibration metadata: Accept={accept}, HTTP={(int)response.StatusCode}, type={response.Content.Headers.ContentType}, bytes={body.Length}");
                if (!response.IsSuccessStatusCode)
                {
                    string masked = body.Replace(requestId, "<masked>", StringComparison.OrdinalIgnoreCase)
                        .Replace(machine.SerialNumber, "<masked>", StringComparison.OrdinalIgnoreCase);
                    masked = System.Text.RegularExpressions.Regex.Replace(masked, @"https?://[^\s""<>]+", "<url>");
                    Console.WriteLine(masked[..Math.Min(masked.Length, 240)]);
                }
            }
            return;
        }

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
        Console.WriteLine($"Color backend: {color.Snapshot.Backend}");
        var internalDisplay = DisplayService.FindInternal(DisplayService.EnumerateActive());
        if (internalDisplay is not null)
            Console.WriteLine($"Color flags: error={internalDisplay.AdvancedColorError}, raw=0x{internalDisplay.AdvancedColor?.value:X8}, mode={internalDisplay.AdvancedColor?.activeColorMode}, HDR={internalDisplay.HdrEnabled}, ACM={internalDisplay.AcmEnabled}");
        Console.WriteLine($"Audio: uplink={audio.Snapshot.Uplink}, downlink={audio.Snapshot.Downlink}, special={audio.Snapshot.Special}, voiceId={audio.Snapshot.VoiceId}, registered={audio.Snapshot.VoiceIdRegistered}");
        Console.WriteLine("No color/audio mode writes, recording, downloads, or persistent configuration writes were invoked. TCON capability reads may temporarily gate an SPB session.");
    }
}
