using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace YogaITS.Tray.Services;

// The SDK embeds ApplicationIcon in the executable's native icon resources.
// Theme variants are also embedded for live notification-area updates. Every consumer
// owns its HICON; no loose ICO or temporary file is needed.
internal sealed class ApplicationIcon : SafeHandleZeroOrMinusOneIsInvalid
{
    private ApplicationIcon(nint icon) : base(ownsHandle: true) => SetHandle(icon);

    public static bool UseLightForeground()
    {
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (SystemParametersInfo(0x0042, contrast.Size, ref contrast, 0) && (contrast.Flags & 1) != 0)
            return SelectLightForeground(null, GetSysColor(8)); // COLOR_WINDOWTEXT
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return SelectLightForeground(key?.GetValue("SystemUsesLightTheme") as int?);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return SelectLightForeground(null);
        }
    }

    internal static bool SelectLightForeground(int? systemUsesLightTheme, uint? contrastText = null)
    {
        if (contrastText is uint color)
            return 299 * (color & 255) + 587 * ((color >> 8) & 255) + 114 * ((color >> 16) & 255) >= 128000;
        // Follow the shell, independently of the application's color mode.
        return systemUsesLightTheme != 1;
    }

    public static ApplicationIcon LoadThemed(bool small, bool lightForeground, uint dpi = 96)
    {
        string name = lightForeground ? "YogaITS.Icon.Dark" : "YogaITS.Icon.Light";
        using var resource = typeof(ApplicationIcon).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少内置主题图标：" + name);
        using var stream = new MemoryStream();
        resource.CopyTo(stream);
        byte[] ico = stream.ToArray();
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
            throw new InvalidDataException("内置图标目录无效。");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        if (count == 0 || 6L + count * 16L > ico.Length) throw new InvalidDataException("内置图标目录不完整。");
        int edge = GetSystemMetricsForDpi(small ? 49 : 11, dpi == 0 ? 96 : dpi);
        if (edge <= 0) edge = small ? 16 : 32;
        int selected = -1, best = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int width = ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16];
            // Prefer an exact size, then downscale the nearest larger frame.
            int distance = width >= edge ? width - edge : 1000 + edge - width;
            if (distance < best) { selected = i; best = distance; }
        }
        var entry = ico.AsSpan(6 + selected * 16, 16);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        if (length == 0 || offset + (ulong)length > (ulong)ico.Length)
            throw new InvalidDataException("内置图标帧不完整。");
        byte[] frame = ico.AsSpan((int)offset, (int)length).ToArray();
        nint handle = CreateIconFromResourceEx(frame, length, true, 0x00030000, edge, edge, 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载内置主题图标。");
        return new ApplicationIcon(handle);
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size, Flags; public nint Scheme; }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconFromResourceEx(byte[] bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version, int width, int height, uint flags);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(string path, int index, out nint largeIcon, out nint smallIcon, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
