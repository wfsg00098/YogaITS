using System.Buffers.Binary;
using System.Text;
using YogaITS.Tray.Services.Color;
using YogaITS.Tray.UI;

internal static class FactoryCalibrationChecks
{
    public static async Task<int> RunAsync(Func<string, string, int, string, byte[]> profile)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Factory calibration: " + message);
            checks++;
        }
        string root = Path.Combine(Path.GetTempPath(), "YogaITS-Calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var machine = new MachineIdentity("TEST", "SERIAL", "TEST", "");
            byte[] bytes = profile("DIFFERENT", "OTHER", 80, "OTHER");
            string path = Path.Combine(root, "TPLCD_xxxx_DiCoM_Dim.ICC");
            File.WriteAllBytes(path, bytes);
            var result = IccInspector.ValidateFactoryDirectory(root, machine, "PANEL");
            Check(IccInspector.ValidateFactoryDirectory(root, null, null).Ok, "Discovery does not require SMBIOS identity or panel metadata.");
            Check(result.Ok && result.ProfileCount == 1 && !result.NativePresent, "Single non-Native mode needs no fixed package composition.");
            Check(result.Profiles.Single().Mode == "DiCoM_Dim", "Compound suffix and original casing survive discovery.");
            Check(SettingsOptionText.ColorLabel("DiCoM_Dim") == "暗室医学影像模拟 (DICOM Dim SIM)", "Known display labels ignore case.");
            foreach (string mode in new[] { "Default", "Native", "sRGB", "DisplayP3", "AdobeRGB", "DCIP3", "REC709", "DICOM_Office", "FooBar", "Foo_Bar", "Unknown" })
                File.WriteAllBytes(Path.Combine(root, "LENxxxx_" + mode + ".icm"), bytes);
            result = IccInspector.ValidateFactoryDirectory(root, machine, null);
            Check(result.Ok && result.Profiles.Count == 12, "All actual ICC/ICM modes are discovered without a model or identity filter.");
            Check(result.Profiles.Take(7).Select(item => item.Mode).SequenceEqual(new[] { "Native", "sRGB", "DisplayP3", "AdobeRGB", "DCIP3", "REC709", "Default" }), "Existing mode order is preserved.");
            Check(result.Profiles.Any(item => item.Mode == "Unknown") && SettingsOptionText.ColorLabel("Foo_Bar") == "Foo_Bar", "Unknown is a legitimate suffix, never an exclusion sentinel.");
            foreach (var item in result.Profiles)
                Check(ProfileService.ModeFromCurrent(ProfileService.ManagedProfileName(item.Mode), result.Profiles) == item.Mode,
                    "Managed profile names round-trip the complete raw mode.");
            Check(ProfileService.ManagedProfileName("Foo_Bar") != ProfileService.ManagedProfileName("FooBar"), "Different underscore suffixes cannot collide.");
            Check(ProfileService.ModeFromCurrent("YOGAITS_DICOM_DIM.ICM", result.Profiles) == "DiCoM_Dim", "Windows readback resolves to original factory casing.");

            int Tag(byte[] data, string name)
            {
                for (int i = 0; i < BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128, 4)); i++)
                    if (Encoding.ASCII.GetString(data, 132 + i * 12, 4) == name) return 132 + i * 12;
                throw new InvalidOperationException("Missing fixture tag.");
            }
            byte[] broken = bytes.ToArray();
            int mhc = Tag(broken, "MHC2");
            int mhcOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(broken.AsSpan(mhc + 4, 4));
            BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan(mhcOffset + 20, 4), 999999);
            string bad = Path.Combine(root, "LENxxxx_Bad.icm");
            File.WriteAllBytes(bad, broken);
            result = IccInspector.ValidateFactoryDirectory(root, machine, "PANEL");
            Check(!result.Ok && result.CorruptProfiles?.SequenceEqual(new[] { bad }) == true && result.Profiles.Count == 12,
                "Bad matrix data is identified separately from good files for redownload.");

            byte[] metadataOnly = bytes.ToArray();
            int cxf = Tag(metadataOnly, "CxF ");
            BinaryPrimitives.WriteUInt32BigEndian(metadataOnly.AsSpan(cxf + 4, 4), uint.MaxValue);
            int meta = Tag(metadataOnly, "meta");
            BinaryPrimitives.WriteUInt32BigEndian(metadataOnly.AsSpan(meta + 4, 4), uint.MaxValue);
            File.WriteAllBytes(path, metadataOnly);
            Check(IccCalibrationData.Validate(path, out _) && !IccMetaBrightness.Read(path).Ok, "Broken descriptive metadata does not discard intact calibration data.");
            byte[] noMhc = bytes.ToArray();
            Encoding.ASCII.GetBytes("desc").CopyTo(noMhc, mhc);
            File.WriteAllBytes(path, noMhc);
            Check(IccCalibrationData.Validate(path, out _), "Standard matrix/TRC profiles do not require MHC2.");

            var corruptResult = result with { Fingerprint = "stable" };
            int calls = 0;
            var recovered = await CalibrationDownloadPolicy.ValidateAsync(_ => { calls++; return Task.FromResult(corruptResult); }, default);
            Check(recovered.Ok && calls == 4 && recovered.Profiles.Count == 12, "Three redownloads precede skipping consistently corrupt files.");
            calls = 0;
            recovered = await CalibrationDownloadPolicy.ValidateAsync(_ =>
                Task.FromResult(++calls == 2 ? corruptResult with { Ok = true, CorruptProfiles = [] } : corruptResult), default);
            Check(recovered.Ok && calls == 2, "A repaired download stops the retry loop immediately.");
            calls = 0;
            recovered = await CalibrationDownloadPolicy.ValidateAsync(_ =>
                Task.FromResult(corruptResult with { Fingerprint = (++calls).ToString() }), default);
            Check(!recovered.Ok && calls == 4, "Changing corrupt payloads cannot be accepted as stable partial results.");
            recovered = await CalibrationDownloadPolicy.ValidateAsync(_ =>
                Task.FromResult(corruptResult with { Profiles = [], ProfileCount = 0 }), default);
            Check(!recovered.Ok, "A package containing no good calibration files is never installed.");
            using var cancellation = new CancellationTokenSource();
            calls = 0;
            bool canceled = false;
            try
            {
                await CalibrationDownloadPolicy.ValidateAsync(_ =>
                {
                    calls++;
                    cancellation.Cancel();
                    return Task.FromResult(corruptResult);
                }, cancellation.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && calls == 1, "Cancellation prevents retries and partial acceptance.");
        }
        finally { Directory.Delete(root, recursive: true); }
        return checks;
    }
}
