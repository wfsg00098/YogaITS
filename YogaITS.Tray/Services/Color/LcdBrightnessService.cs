namespace YogaITS.Tray.Services.Color;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed record LcdBrightnessSnapshot(bool Ok, int Current, IReadOnlyList<int> Supported, string Detail);
internal sealed record LcdBrightnessSetResult(
    bool Ok,
    bool IoctlOk,
    bool Verified,
    int Before,
    int After,
    IReadOnlyList<int> Supported,
    string Detail,
    int Win32Error,
    long SetIoctlMs,
    long VerifyMs,
    string VerifySamples);

internal static class LcdBrightnessService
{
    private const string DevicePath = @"\\.\LCD";
    private const uint IOCTL_GET_SUPPORTED_BRIGHTNESS = 0x00230494;
    private const uint IOCTL_GET_BRIGHTNESS = 0x00230498;
    private const uint IOCTL_SET_BRIGHTNESS = 0x0023049C;

    public static LcdBrightnessSnapshot Query()
    {
        var levels = GetSupported(out string? levelError);
        if (levelError is not null) return new(false, -1, levels, levelError);
        if (!TryGetFresh(out int current, out string? getError)) return new(false, -1, levels, getError ?? "Brightness read failed.");
        return new(true, current, levels, $"LCD brightness={current}; supportedCount={levels.Count}");
    }

    // Factory-profile brightness SET path:
    // - validate target against GET_SUPPORTED
    // - open a fresh \\.\LCD handle for SET
    // - send exactly {0,B,B}, 3 bytes
    // - close immediately after DeviceIoControl
    // Verification is intentionally separate and uses fresh handles with polling; SET success is the authoritative write result.
    public static LcdBrightnessSetResult SetProfileBrightness(int brightness, int verifyTimeoutMs = 1000)
    {
        if (brightness is < 0 or > 100)
            return new(false, false, false, -1, -1, Array.Empty<int>(), "Brightness must be 0..100.", 0, 0, 0, "");

        var levels = GetSupported(out string? levelError);
        if (levelError is not null)
            return new(false, false, false, -1, -1, levels, levelError, Marshal.GetLastWin32Error(), 0, 0, "");
        if (!levels.Contains(brightness))
            return new(false, false, false, -1, -1, levels, $"Target brightness {brightness} is not in the panel-supported brightness list.", 0, 0, 0, "");

        _ = TryGetFresh(out int before, out _);

        byte b = checked((byte)brightness);
        byte[] input = { 0, b, b };
        var setSw = Stopwatch.StartNew();
        int setError = 0;
        bool ioctlOk;
        IntPtr h = OpenLcd();
        if (h == NativeMethods.INVALID_HANDLE_VALUE)
        {
            setSw.Stop();
            int e = Marshal.GetLastWin32Error();
            return new(false, false, false, before, before, levels, $"CreateFile({DevicePath}) for SET failed: {e} ({new Win32Exception(e).Message})", e, setSw.ElapsedMilliseconds, 0, "");
        }
        try
        {
            ioctlOk = NativeMethods.DeviceIoControl(h, IOCTL_SET_BRIGHTNESS,
                input, (uint)input.Length, null, 0, out uint _, IntPtr.Zero);
            if (!ioctlOk) setError = Marshal.GetLastWin32Error();
        }
        finally
        {
            NativeMethods.CloseHandle(h);
            setSw.Stop();
        }

        if (!ioctlOk)
        {
            return new(false, false, false, before, before, levels,
                $"SET IOCTL 0x{IOCTL_SET_BRIGHTNESS:X8} failed: {setError} ({new Win32Exception(setError).Message})",
                setError, setSw.ElapsedMilliseconds, 0, "");
        }

        var verifySw = Stopwatch.StartNew();
        var samples = new List<string>();
        int after = before;
        bool verified = false;
        int[] checkpoints = { 0, 5, 10, 20, 35, 50, 75, 100, 150, 200, 300, 500, 750, 1000 };
        int lastCheckpoint = 0;
        foreach (int checkpoint in checkpoints)
        {
            if (checkpoint > verifyTimeoutMs) break;
            int wait = checkpoint - lastCheckpoint;
            if (wait > 0) Thread.Sleep(wait);
            lastCheckpoint = checkpoint;
            if (TryGetFresh(out int current, out string? err))
            {
                after = current;
                samples.Add($"{checkpoint}ms:{current}");
                if (current == brightness) { verified = true; break; }
            }
            else
            {
                samples.Add($"{checkpoint}ms:ERR({err})");
            }
        }
        verifySw.Stop();

        string sampleText = string.Join(", ", samples);
        string detail = verified
            ? $"SET IOCTL PASS; brightness settled {before} -> {after}; payload=00 {b:X2} {b:X2}; samples=[{sampleText}]"
            : $"SET IOCTL PASS, but brightness did not verify within {verifyTimeoutMs} ms; requested={brightness}, before={before}, last={after}; samples=[{sampleText}]";

        return new(verified, true, verified, before, after, levels, detail, 0, setSw.ElapsedMilliseconds, verifySw.ElapsedMilliseconds, sampleText);
    }

    private static IReadOnlyList<int> GetSupported(out string? error)
    {
        IntPtr h = OpenLcd();
        if (h == NativeMethods.INVALID_HANDLE_VALUE)
        {
            int e = Marshal.GetLastWin32Error();
            error = $"CreateFile({DevicePath}) failed: {e} ({new Win32Exception(e).Message})";
            return Array.Empty<int>();
        }
        try
        {
            byte[] output = new byte[256];
            bool ok = NativeMethods.DeviceIoControl(h, IOCTL_GET_SUPPORTED_BRIGHTNESS,
                null, 0, output, (uint)output.Length, out uint returned, IntPtr.Zero);
            if (!ok)
            {
                int e = Marshal.GetLastWin32Error();
                error = $"IOCTL 0x{IOCTL_GET_SUPPORTED_BRIGHTNESS:X8} failed: {e} ({new Win32Exception(e).Message})";
                return Array.Empty<int>();
            }
            error = null;
            return output.Take((int)Math.Min(returned, (uint)output.Length)).Select(x => (int)x).ToArray();
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    private static bool TryGetFresh(out int brightness, out string? error)
    {
        IntPtr h = OpenLcd();
        if (h == NativeMethods.INVALID_HANDLE_VALUE)
        {
            int e = Marshal.GetLastWin32Error();
            brightness = -1;
            error = $"CreateFile({DevicePath}) for GET failed: {e} ({new Win32Exception(e).Message})";
            return false;
        }
        try
        {
            byte[] output = new byte[3];
            bool ok = NativeMethods.DeviceIoControl(h, IOCTL_GET_BRIGHTNESS,
                null, 0, output, (uint)output.Length, out uint returned, IntPtr.Zero);
            if (!ok || returned < 2)
            {
                int e = Marshal.GetLastWin32Error();
                brightness = -1;
                error = ok ? $"IOCTL GET returned only {returned} bytes." : $"IOCTL 0x{IOCTL_GET_BRIGHTNESS:X8} failed: {e} ({new Win32Exception(e).Message})";
                return false;
            }
            brightness = output[1];
            error = null;
            return true;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    private static IntPtr OpenLcd() => NativeMethods.CreateFileW(DevicePath,
        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
        0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
}
