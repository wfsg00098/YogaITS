using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;

namespace YogaITS.Tray.UI;

// UI-thread registry plus out-of-context WinEvents. No code is injected into
// other applications. Each target gets its OWN list from the current Z order.
internal static class CaptureWindowExclusion
{
    private sealed class Registration(Action changed)
    {
        public Action Changed { get; } = changed;
        public CaptureExclusionCache Cache { get; } = new();
        public long Queries { get; set; }
    }
    private static readonly Dictionary<nint, Registration> Windows = [];
    private static long _revision;
    private static readonly List<nint> Hooks = [];
    private static readonly WinEventProc Callback = WindowEvent;
    public static bool Supported { get; } =
        ApiInformation.IsTypePresent("Windows.Graphics.Capture.IDisplayGraphicsCaptureSession");
    public static string Status => Supported ? "按窗口层级排除上层，允许其他应用截图" : "不支持会话级排除，使用原版 UI";

    public static void Register(nint hwnd, Action changed)
    {
        if (!Windows.TryAdd(hwnd, new(changed))) return;
        try
        {
            // Keep external screenshot tools working; never change affinity on
            // a foreign window. Unsupported session APIs use native UI instead.
            if (IsWindow(hwnd) && !SetWindowDisplayAffinity(hwnd, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot clear preview capture affinity.");
            if (Hooks.Count == 0 && Supported)
            {
                Hook(0x0003, 0x0003); // Foreground.
                Hook(0x000B, 0x000B); // One safety recheck after moving, not during every mouse move.
                Hook(0x0016, 0x0017); // Minimize start/end.
                Hook(0x8000, 0x8018); // Filtered window lifecycle/order; geometry events are ignored.
            }
            Invalidate();
        }
        catch
        {
            Windows.Remove(hwnd);
            if (Windows.Count == 0) Unhook();
            throw;
        }
    }

    public static void Unregister(nint hwnd)
    {
        if (!Windows.Remove(hwnd)) return;
        if (Windows.Count == 0) Unhook();
        else Invalidate();
    }

    public static void Invalidate()
    {
        _revision++;
        Notify();
    }

    public static long QueryCount(nint target) => Windows.TryGetValue(target, out var window) ? window.Queries : 0;

    public static bool TrySnapshot(nint target, out nint[] excluded)
    {
        excluded = [];
        if (!Windows.TryGetValue(target, out var window)) return false;
        long now = Environment.TickCount64;
        if (window.Cache.TryRead(_revision, now, out excluded)) return true;
        long revision = _revision;
        window.Queries++;
        if (!IsWindow(target) || !CaptureExclusionPolicy.TryCreate(target, Windows.Keys, FrontToBack(target), out excluded))
            return false;
        // A DWM/COM call can pump an event while enumerating. Do not mark a
        // pre-event snapshot as belonging to the newer scene revision.
        if (_revision != revision) return false;
        window.Cache.Store(revision, now, excluded);
        return true;
    }

    private static IEnumerable<CaptureWindowEntry> FrontToBack(nint target)
    {
        var seen = new HashSet<nint>();
        for (nint hwnd = GetTopWindow(0); hwnd != 0 && seen.Count < 4096 && seen.Add(hwnd); hwnd = GetWindow(hwnd, 2))
        {
            if (hwnd == target) { yield return new(hwnd); yield break; }
            bool visible = IsWindowVisible(hwnd);
            bool minimized = visible && IsIconic(hwnd);
            bool cloaked = visible && !minimized &&
                DwmGetWindowAttribute(hwnd, 14, out uint value, sizeof(uint)) >= 0 && value != 0;
            yield return new(hwnd, visible, minimized, cloaked);
        }
    }

    private static void Hook(uint first, uint last)
    {
        nint hook = SetWinEventHook(first, last, 0, Callback, 0, 0, 0); // WINEVENT_OUTOFCONTEXT.
        if (hook == 0)
        {
            int error = Marshal.GetLastWin32Error();
            Unhook();
            throw new System.ComponentModel.Win32Exception(error, "Cannot track desktop window order.");
        }
        Hooks.Add(hook);
    }

    private static void Unhook()
    {
        foreach (nint hook in Hooks) _ = UnhookWinEvent(hook);
        Hooks.Clear();
    }

    private static void WindowEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (!CaptureExclusionPolicy.IsWindowEvent(eventId, objectId, childId)) return;
        if (eventId >= 0x8000)
        {
            // Ignore child-HWND lifecycle/arrange traffic from XAML and other
            // applications. Destroyed HWNDs cannot be queried for ancestry;
            // only a previously excluded/owned HWND can invalidate that scene.
            if (eventId == 0x8001)
            {
                if (!Windows.ContainsKey(hwnd) && !Windows.Values.Any(window => window.Cache.Contains(hwnd))) return;
            }
            else if (hwnd != 0 && GetAncestor(hwnd, 2) != hwnd) return; // GA_ROOT.
        }
        Invalidate();
    }

    private static void Notify()
    {
        // Callbacks only coalesce dispatcher wake-ups. Actual enumeration and
        // session mutations happen later on the owning window's UI dispatcher.
        foreach (var window in Windows.Values.ToArray())
        {
            try { window.Changed(); }
            catch (Exception ex) { CaptureDiagnostics.Write("Window-order notification: " + ex.Message); }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll", ExactSpelling = true)] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out uint value, int size);
}