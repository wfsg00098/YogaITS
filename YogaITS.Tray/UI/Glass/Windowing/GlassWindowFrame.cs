using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.UI.Glass.Windowing;

// One rectangle for the HWND client, captured layer and optical window shape.
// Do not set a window region: it disables DWM's native rounded-frame treatment.
internal sealed class GlassWindowFrame
{
    private bool _enabled;
    private bool _changing;

    public void SetEnabled(nint hwnd, bool enabled)
    {
        if (_changing || _enabled == enabled) return;
        _changing = true;
        bool previous = _enabled;
        try
        {
            _enabled = enabled;
            if (!SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0037))
            {
                _enabled = previous;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot recalculate the glass window frame.");
            }
        }
        finally { _changing = false; }
    }

    public nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        bool fullClient = _enabled && message == 0x0083 && lParam != 0 && !IsZoomed(hwnd) && !IsIconic(hwnd);
        // RECT is the first member for both WM_NCCALCSIZE payload variants.
        // Save it before WinUI calculates its conventional non-client strips.
        var proposed = fullClient ? Marshal.PtrToStructure<RectI>(lParam) : default;
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (!fullClient) return result;
        Marshal.StructureToPtr(proposed, lParam, false);
        return 0;
    }

    public nint HitTest(nint hwnd, nint position, nint nativeResult)
    {
        if (!_enabled || IsZoomed(hwnd) ||
            ((long)GetWindowLongPtrW(hwnd, -16) & 0x00040000L) == 0) return nativeResult;
        var origin = new PointI();
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref origin)) return nativeResult;
        int x = (short)((long)position & 0xFFFF) - origin.X;
        int y = (short)(((long)position >> 16) & 0xFFFF) - origin.Y;
        int grip = Math.Max(1, (int)Math.Round(4 * GetDpiForWindow(hwnd) / 96.0));
        return GlassEdgePolicy.ResizeHitTest(x, y, client.Right, client.Bottom, grip, nativeResult);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out RectI rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref PointI point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
}
