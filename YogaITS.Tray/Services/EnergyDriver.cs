using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

// Energy settings use FILE_READ_DATA | FILE_WRITE_DATA, as in LLT Drivers.GetEnergy.
// Keyboard backlight retains its separate GENERIC_READ/WRITE device contract.
internal static class EnergyDriver
{
    public static SafeFileHandle Open()
    {
        var handle = CreateFileW(@"\\.\EnergyDrv", 0x00000003, 0x00000003, 0, 3, 0x80, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "无法打开 Lenovo EnergyDrv");
    }

    public static uint SendCode(SafeFileHandle handle, uint ioctl, uint command)
    {
        if (!DeviceIoControl(handle, ioctl, ref command, sizeof(uint),
            out uint output, sizeof(uint), out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"EnergyDrv DeviceIoControl 失败：0x{ioctl:X8}");
        return output;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        nint security, uint disposition, uint attributes, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioctl, ref uint input,
        int inputLength, out uint output, int outputLength, out uint returned, nint overlapped);
}
