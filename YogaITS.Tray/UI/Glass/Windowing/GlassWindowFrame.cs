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
        int grip = _enabled ? ResizeGrip(hwnd) : 0;
        if (grip == 0) return nativeResult;
        var origin = new PointI();
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref origin)) return nativeResult;
        int x = (short)((long)position & 0xFFFF) - origin.X;
        int y = (short)(((long)position >> 16) & 0xFFFF) - origin.Y;
        return GlassEdgePolicy.ResizeHitTest(x, y, client.Right, client.Bottom, grip, nativeResult);
    }

    public bool TryHandleContentMessage(nint parent, uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message is not (0x0084 or 0x0020 or 0x00A1 or 0x00A3) || !_enabled || ResizeGrip(parent) == 0)
            return false;
        nint hit = message == 0x0020 ? (short)((long)lParam & 0xFFFF) : HitTest(parent, lParam, 1);
        int cursor = GlassEdgePolicy.ResizeCursor(hit);
        if (cursor == 0) return false;
        if (message == 0x0084) // WM_NCHITTEST
        {
            // A full-client XAML site must own this non-client hit. Returning
            // HTTRANSPARENT does not forward its mouse input to the parent.
            result = hit;
        }
        else if (message == 0x0020) // WM_SETCURSOR
        {
            nint handle = LoadCursorW(0, cursor);
            if (handle == 0) return false;
            _ = SetCursor(handle);
            result = 1;
        }
        else // WM_NCLBUTTONDOWN / WM_NCLBUTTONDBLCLK
        {
            if (GlassEdgePolicy.ResizeCursor((nint)wParam) == 0) return false;
            // The HWND owning the full-client site is a child. Explicitly start
            // sizing its parent instead of letting WinUI consume the operation.
            _ = ReleaseCapture();
            result = SendMessageW(parent, message, (nuint)hit, lParam);
        }
        return true;
    }

    internal static int ResizeGrip(nint hwnd) => IsZoomed(hwnd) || IsIconic(hwnd) ||
        ((long)GetWindowLongPtrW(hwnd, -16) & 0x00040000L) == 0 ? 0 :
        Math.Max(1, (int)Math.Round(4 * GetDpiForWindow(hwnd) / 96.0));

    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out RectI rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref PointI point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
}
