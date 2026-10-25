namespace YogaITS.Tray.UI.Glass.Capture;

// Accessed on the UI owner, including callbacks dispatched from old workers.
internal sealed class CaptureOperationState
{
    private long _revision;
    private bool _active;
    public long Begin() { _active=true; return ++_revision; }
    public void Retire() { _active=false; ++_revision; }
    public void RunIfCurrent(long revision,Action action)
    {
        if(_active && revision==_revision) action();
    }
}
