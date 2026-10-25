namespace YogaITS.Tray.Services.Color;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal sealed record GamutApplyResult(
    bool Ok,
    string Mode,
    string ManagedProfileName,
    string? StandardBefore,
    string? StandardAfter,
    int TargetBrightness,
    bool BrightnessRequested,
    bool BrightnessSetOk,
    int BrightnessAfter,
    bool DwmRefreshOk,
    int DwmRefreshReturnCode,
    long ElapsedMs,
    string Message);

internal static class GamutApplyService
{
    public static GamutApplyResult Apply(DisplayInfo requestedDisplay, GamutProfile source, MachineIdentity? machine, bool applyBrightness = true)
    {
        var sw = Stopwatch.StartNew();
        string managedName = ProfileService.ManagedProfileName(source.Mode);
        string? standardBefore = null;
        string? standardAfter = null;
        int targetBrightness = -1;
        bool brightnessOk = false;
        int brightnessAfter = -1;
        DwmRefreshResult? dwm = null;
        string? tempDir = null;

        try
        {
            DisplayInfo? d = DisplayService.RefreshDisplayIdentity(requestedDisplay);
            if (d is null) return Fail("Target display changed/disappeared before Apply.");
            if (!d.IsInternalOutput) return Fail("Target is not an internal/embedded display path.");
            if (d.IsBlocked) return Fail("BLOCKED: HDR/ACM/WCG is active or Advanced Color state is unreadable.");
            if (string.IsNullOrWhiteSpace(d.WcsDeviceId)) return Fail("Dynamic WCS monitor DeviceID discovery failed: " + d.WcsDiscovery);

            if (!IccCalibrationData.Validate(source.Path, out string calibrationDetail))
                return Fail("Factory ICC calibration data is invalid: " + calibrationDetail);

            var brMeta = IccMetaBrightness.Read(source.Path);
            // Recommended brightness is optional metadata, never a condition
            // for using an otherwise complete calibration profile.
            applyBrightness &= brMeta.Ok;
            if (brMeta.Ok) targetBrightness = brMeta.Brightness;

            standardBefore = ProfileService.GetStandardDefault(d);
            string currentMode = ProfileService.ModeFromCurrent(standardBefore);
            if (currentMode.Equals(source.Mode, StringComparison.OrdinalIgnoreCase))
            {
                if (applyBrightness)
                {
                    var brSame = LcdBrightnessService.SetProfileBrightness(targetBrightness, 1000);
                    brightnessOk = brSame.IoctlOk;
                    brightnessAfter = brSame.After;
                }
                else
                {
                    var current = LcdBrightnessService.Query();
                    brightnessOk = true;
                    brightnessAfter = current.Ok ? current.Current : -1;
                }
                dwm = DwmRefreshService.RefreshCurrentDisplayConfiguration();
                sw.Stop();
                bool sameOk = brightnessOk && dwm.Ok;
                return new(sameOk, source.Mode, managedName, standardBefore, standardBefore,
                    targetBrightness, applyBrightness, brightnessOk, brightnessAfter, dwm.Ok, dwm.ReturnCode, sw.ElapsedMilliseconds,
                    sameOk
                        ? (applyBrightness
                            ? "Target gamut is already STANDARD; brightness reasserted and DWM refresh completed."
                            : "Target gamut is already STANDARD; brightness unchanged and DWM refresh completed.")
                        : $"Target gamut already active, but refresh incomplete: BrightnessSET={brightnessOk}, DWM={dwm.Ok}.");
            }

            string installedPath = Path.Combine(ProfileService.ColorDirectory, managedName);

            // Fixed YogaITS managed name, refreshed on every switch. This keeps Factory ICCs immutable
            // and guarantees the Windows registered copy is byte-identical to the validated source.
            _ = NativeMethods.ColorProfileRemoveDisplayAssociation(
                d.ProfileScope, managedName, d.AdapterId, d.SourceId, false);

            if (File.Exists(installedPath))
            {
                bool un = NativeMethods.UninstallColorProfileW(null, managedName, true);
                if (!un && File.Exists(installedPath))
                {
                    int err = Marshal.GetLastWin32Error();
                    return Fail($"UninstallColorProfileW failed: Win32={err} ({new Win32Exception(err).Message}).");
                }
            }

            tempDir = Path.Combine(Path.GetTempPath(), "YogaITS_GamutApply", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempProfile = Path.Combine(tempDir, managedName);
            File.Copy(source.Path, tempProfile, true);

            if (!NativeMethods.InstallColorProfileW(null, tempProfile))
            {
                int err = Marshal.GetLastWin32Error();
                return Fail($"InstallColorProfileW failed: Win32={err} ({new Win32Exception(err).Message}).");
            }
            if (!File.Exists(installedPath) || !FilesEqual(source.Path, installedPath))
                return Fail("Installed YogaITS managed ICC is not byte-identical to the validated Factory source.");

            int hrAdd = NativeMethods.ColorProfileAddDisplayAssociation(
                d.ProfileScope, managedName, d.AdapterId, d.SourceId, false, false);
            if (hrAdd < 0)
                return Fail($"ColorProfileAddDisplayAssociation failed: HR=0x{unchecked((uint)hrAdd):X8}.");

            int hrStd = NativeMethods.ColorProfileSetDisplayDefaultAssociation(
                d.ProfileScope, managedName,
                NativeMethods.CPT_ICC, NativeMethods.CPST_STANDARD_DISPLAY_COLOR_MODE,
                d.AdapterId, d.SourceId);
            standardAfter = ProfileService.GetStandardDefault(d);
            if (hrStd < 0 || !ProfileService.SameFile(standardAfter, managedName))
                return Fail($"STANDARD default failed: HR=0x{unchecked((uint)hrStd):X8}, readback={Path.GetFileName(standardAfter ?? "<none>")}.");

            bool wcs = NativeMethods.WcsSetDefaultColorProfile(
                NativeMethods.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                d.WcsDeviceId,
                NativeMethods.CPT_ICC,
                NativeMethods.CPST_RGB_WORKING_SPACE,
                0,
                managedName);
            if (!wcs)
            {
                int err = Marshal.GetLastWin32Error();
                return Fail($"WcsSetDefaultColorProfile failed: Win32={err} ({new Win32Exception(err).Message}).");
            }

            if (applyBrightness)
            {
                var br = LcdBrightnessService.SetProfileBrightness(targetBrightness, 1000);
                brightnessOk = br.IoctlOk;
                brightnessAfter = br.After;
                if (!brightnessOk)
                    return Fail("Profile brightness SET failed: " + br.Detail);
            }
            else
            {
                var current = LcdBrightnessService.Query();
                brightnessOk = true;
                brightnessAfter = current.Ok ? current.Current : -1;
            }

            // Proven missing step: make DWM re-read/apply the newly selected ICC/MHC state.
            // C1 was stable and did not visibly blink on the validation machine.
            dwm = DwmRefreshService.RefreshCurrentDisplayConfiguration();
            if (!dwm.Ok)
                return Fail("ICC/WCS/brightness completed, but DWM refresh failed: " + dwm.Message);

            sw.Stop();
            return new(true, source.Mode, managedName, standardBefore, standardAfter,
                targetBrightness, applyBrightness, brightnessOk, brightnessAfter, true, dwm.ReturnCode,
                sw.ElapsedMilliseconds,
                applyBrightness
                    ? "PASS: validated Factory ICC -> refresh managed registration -> associate/STANDARD/WCS -> profile brightness -> C1 DWM refresh."
                    : "PASS: validated Factory ICC -> refresh managed registration -> associate/STANDARD/WCS -> keep current brightness -> C1 DWM refresh.");
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempDir))
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        GamutApplyResult Fail(string message)
        {
            sw.Stop();
            return new(false, source.Mode, managedName, standardBefore, standardAfter,
                targetBrightness, applyBrightness, brightnessOk, brightnessAfter,
                dwm?.Ok ?? false, dwm?.ReturnCode ?? int.MinValue,
                sw.ElapsedMilliseconds, message);
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }
}
