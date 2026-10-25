namespace YogaITS.Tray.UI.Glass.Animation;

internal sealed class SelectionMotionState
{
    public const double SpeedMultiplier = 3;
    private int _revision;
    public bool IsMoving { get; private set; }
    public int Begin(bool moving) { IsMoving = moving; return ++_revision; }
    public bool Complete(int revision)
    {
        if (revision != _revision || !IsMoving) return false;
        IsMoving = false;
        return true;
    }
    public static TimeSpan Period(bool leading, bool dragging) => TimeSpan.FromMilliseconds(
        (dragging ? (leading ? 105 : 155) : (leading ? 265 : 410)) / SpeedMultiplier);
}
