namespace YogaITS.Tray.UI.Glass.Capture;

// Topology changes invalidate immediately; a lazy one-second recheck covers
// missed WinEvents without a polling timer or a Win32 walk on every frame.
internal sealed class CaptureExclusionCache
{
    private nint[] _windows = [];
    private long _revision, _checkedAt;
    private bool _valid;

    public bool TryRead(long revision, long nowMilliseconds, out nint[] windows)
    {
        windows = _windows;
        return _valid && revision == _revision && nowMilliseconds >= _checkedAt &&
            nowMilliseconds - _checkedAt < 1000;
    }

    public void Store(long revision, long nowMilliseconds, nint[] windows)
    {
        _revision = revision; _checkedAt = nowMilliseconds; _windows = windows; _valid = true;
    }

    public bool Contains(nint hwnd) => _valid && Array.IndexOf(_windows, hwnd) >= 0;
}