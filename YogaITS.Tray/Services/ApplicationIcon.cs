using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YogaITS.Tray.Services;

// The SDK embeds ApplicationIcon in the executable's native icon resources.
// Every consumer owns its extracted HICON; no loose ICO or temporary file is needed.
internal sealed class ApplicationIcon : SafeHandleZeroOrMinusOneIsInvalid
{
    private ApplicationIcon(nint icon) : base(ownsHandle: true) => SetHandle(icon);

    public static ApplicationIcon Load(bool small, string? executablePath = null)
    {
        executablePath ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定应用图标资源路径。");
        uint count = ExtractIconEx(executablePath, 0, out nint largeIcon, out nint smallIcon, 1);
        int error = Marshal.GetLastWin32Error();
        nint selected = small ? smallIcon : largeIcon;
        nint unused = small ? largeIcon : smallIcon;
        if (unused != 0) _ = DestroyIcon(unused);
        if (count == 0 || count == uint.MaxValue || selected == 0)
        {
            if (selected != 0) _ = DestroyIcon(selected);
            throw new Win32Exception(error, "无法从程序内置资源加载图标。");
        }
        return new ApplicationIcon(selected);
    }

    protected override bool ReleaseHandle() => DestroyIcon(handle);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(string path, int index, out nint largeIcon, out nint smallIcon, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
