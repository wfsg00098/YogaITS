using System.Runtime.InteropServices;
using System.Text;
using Windows.UI;

namespace YogaITS.Tray.UI.Glass.Backgrounds;

internal sealed record WallpaperSnapshot(string? Path, ImageRect Bounds, WallpaperPosition Position, Color Color);

// Read only: per-monitor image, native positioning and background color.
internal static class DesktopWallpaperProvider
{
    public static WallpaperSnapshot Read(nint hwnd)
    {
        var monitor = new MonitorInfo { Size=(uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd,2),ref monitor))
            throw new InvalidOperationException("无法读取显示器位置。");
        var bounds = Rect(monitor.Monitor);
        object? shell = null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"),true)!);
            var wallpaper=(IDesktopWallpaper)shell!;
            Marshal.ThrowExceptionForHR(wallpaper.GetPosition(out var position));
            Marshal.ThrowExceptionForHR(wallpaper.GetBackgroundColor(out uint color));
            Marshal.ThrowExceptionForHR(wallpaper.GetMonitorDevicePathCount(out uint count));
            string? path=null;
            for(uint i=0;i<count;i++)
            {
                Marshal.ThrowExceptionForHR(wallpaper.GetMonitorDevicePathAt(i,out nint id));
                try
                {
                    if(wallpaper.GetMonitorRECT(id,out var rect)<0 || Rect(rect)!=bounds) continue;
                    Marshal.ThrowExceptionForHR(wallpaper.GetWallpaper(id,out nint file));
                    try { path=Marshal.PtrToStringUni(file); }
                    finally { Marshal.FreeCoTaskMem(file); }
                    break;
                }
                finally { Marshal.FreeCoTaskMem(id); }
            }
            if(position==WallpaperPosition.Span)
                bounds=new(GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79));
            return new(string.IsNullOrWhiteSpace(path)?null:path,bounds,position,
                Color.FromArgb(255,(byte)color,(byte)(color>>8),(byte)(color>>16)));
        }
        catch(Exception ex) when(ex is COMException or InvalidCastException)
        {
            var path=new StringBuilder(32768);
            if(!SystemParametersInfoW(0x73,(uint)path.Capacity,path,0)) throw;
            return new(path.Length==0?null:path.ToString(),bounds,WallpaperPosition.Fill,Color.FromArgb(255,0,0,0));
        }
        finally { if(shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
    }
    private static ImageRect Rect(RectI value)=>new(value.Left,value.Top,value.Right-value.Left,value.Bottom-value.Top);
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public RectI Monitor,Work; public uint Flags; }
    [ComImport,Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig] int ReservedSetWallpaper(nint id,nint path);
        [PreserveSig] int GetWallpaper(nint id,out nint path);
        [PreserveSig] int GetMonitorDevicePathAt(uint index,out nint id);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT(nint id,out RectI rect);
        [PreserveSig] int ReservedSetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int ReservedSetPosition(WallpaperPosition position);
        [PreserveSig] int GetPosition(out WallpaperPosition position);
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfoW(uint action,uint size,StringBuilder path,uint flags);
}
