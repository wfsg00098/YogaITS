using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services.KeyboardBacklight;

internal sealed class EnergyDriverKeyboardBacklightDevice : IKeyboardBacklightDevice
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private readonly SafeFileHandle _handle;

    private EnergyDriverKeyboardBacklightDevice(SafeFileHandle handle) => _handle = handle;

    public static IKeyboardBacklightDevice Open()
    {
        // Deliberately separate from the battery backend's FILE_READ/WRITE_DATA access.
        var handle = CreateFileW(@"\\.\EnergyDrv", GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            0, OpenExisting, 0, 0);
        if (!handle.IsInvalid) return new EnergyDriverKeyboardBacklightDevice(handle);
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "无法打开键盘背光 EnergyDrv 设备。");
    }

    public uint Read(uint command)
    {
        var (output, length) = Send(command);
        return ReadPayload(output, length);
    }

    internal static uint ReadPayload(ReadOnlySpan<byte> output, uint length)
    {
        if (length < sizeof(uint) || length > output.Length)
            throw new InvalidDataException("键盘背光返回的数据长度无效。");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(output);
    }

    // SET's response is not the state. Only the subsequent GET can confirm it.
    public void Write(uint command) => _ = Send(command);

    private (byte[] Output, uint Length) Send(uint command)
    {
        // A non-null output buffer also accommodates EnergyDrv implementations
        // returning more than a DWORD; only the first DWORD is protocol data.
        var output = new byte[16];
        if (!DeviceIoControl(_handle, KeyboardBacklightPolicy.Ioctl, ref command, sizeof(uint),
            output, output.Length, out uint length, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"键盘背光命令 0x{command:X8} 失败。");
        return (output, length);
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        nint security, uint disposition, uint attributes, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioctl, ref uint input,
        int inputLength, [Out] byte[] output, int outputLength, out uint returned, nint overlapped);
}
