using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace YogaITS.Tray.UI.Glass.Windowing;

// WinUI's input site spans the full client. Register the grips with its input
// router as well as the HWND so content cannot swallow resize cursors/drags.
internal sealed class GlassResizeRegions(Window window) : IDisposable
{
    private static readonly NonClientRegionKind[] Borders =
        [NonClientRegionKind.TopBorder, NonClientRegionKind.BottomBorder, NonClientRegionKind.LeftBorder, NonClientRegionKind.RightBorder];
    private InputNonClientPointerSource? _source;
    private (int Width, int Height, int Grip)? _bounds;

    public void Update(bool enabled)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        int grip = enabled ? GlassWindowFrame.ResizeGrip(hwnd) : 0;
        if (grip == 0 || !GetClientRect(hwnd, out var client) || client.Right <= 2 * grip || client.Bottom <= 2 * grip)
        {
            Clear();
            return;
        }
        var bounds = (client.Right, client.Bottom, grip);
        if (_bounds == bounds) return;
        _source ??= InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        // Remember ownership before writing so a partial failure is cleared too.
        _bounds = bounds;
        _source.SetRegionRects(NonClientRegionKind.TopBorder, [new RectInt32(0, 0, client.Right, grip)]);
        _source.SetRegionRects(NonClientRegionKind.BottomBorder, [new RectInt32(0, client.Bottom - grip, client.Right, grip)]);
        _source.SetRegionRects(NonClientRegionKind.LeftBorder, [new RectInt32(0, grip, grip, client.Bottom - 2 * grip)]);
        _source.SetRegionRects(NonClientRegionKind.RightBorder, [new RectInt32(client.Right - grip, grip, grip, client.Bottom - 2 * grip)]);
    }

    private void Clear()
    {
        if (_bounds is null || _source is null) return;
        foreach (var border in Borders) _source.ClearRegionRects(border);
        _bounds = null;
    }

    public void Dispose() => Clear();

    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out RectI rect);
}
