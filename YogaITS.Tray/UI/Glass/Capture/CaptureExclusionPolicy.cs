namespace YogaITS.Tray.UI.Glass.Capture;

internal readonly record struct CaptureWindowEntry(nint Handle, bool Visible = true, bool Minimized = false, bool Cloaked = false);

// Ordered desktop snapshot -> stable session list. No Win32 calls or UI state.
internal static class CaptureExclusionPolicy
{
    public static bool TryCreate(nint target, IEnumerable<nint> ownedWindows,
        IEnumerable<CaptureWindowEntry> frontToBack, out nint[] excluded)
    {
        excluded = [];
        if (target == 0) return false;
        var windows = new HashSet<nint>(ownedWindows.Where(hwnd => hwnd != 0)) { target };
        foreach (var window in frontToBack)
        {
            // A hidden target is still a valid anchor in the desktop's Z order.
            if (window.Handle == target)
            {
                // Ordering among already-excluded windows does not change the
                // image. Normalization avoids needless configuration changes.
                excluded = windows.OrderBy(hwnd => (long)hwnd).ToArray();
                return true;
            }
            if (window.Handle != 0 && window.Visible && !window.Minimized && !window.Cloaked)
                windows.Add(window.Handle);
        }
        // An incomplete enumeration must not become "exclude the whole desktop".
        return false;
    }

    public static bool IsWindowEvent(uint eventId, int objectId, int childId)
    {
        if (eventId is 0x0003 or 0x000B or 0x0016 or 0x0017) return true; // foreground / move-end / minimize
        if (childId != 0 || objectId is not (0 or -4)) return false;
        if (eventId == 0x8004) return true; // Desktop client/window reorder.
        return objectId == 0 && eventId is 0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x8017 or 0x8018;
    }
}