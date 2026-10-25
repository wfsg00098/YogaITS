namespace YogaITS.Tray.UI;

// Readback and requested/visual selection remain separate until confirmation.
internal sealed class OptionSelectionState
{
    private readonly bool[] _enabled;
    public int Count => _enabled.Length;
    public int SelectedIndex { get; private set; }
    public int? PendingIndex { get; private set; }
    public int DisplayIndex => PendingIndex ?? SelectedIndex;
    public bool IsBusy => PendingIndex.HasValue;
    public OptionSelectionState(int count)
    {
        if (count < 1 || count > 16) throw new ArgumentOutOfRangeException(nameof(count));
        _enabled = Enumerable.Repeat(true, count).ToArray();
    }
    public bool IsEnabled(int index) => index >= 0 && index < Count && _enabled[index];
    public void SetEnabled(int index, bool enabled) => _enabled[index] = enabled;
    public bool Request(int index)
    {
        if (IsBusy || !IsEnabled(index) || index == SelectedIndex) return false;
        PendingIndex = index;
        return true;
    }
    public void Confirm(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        SelectedIndex = index;
        PendingIndex = null;
    }
    // Pointer release never chooses an unrelated neighboring setting.
    // The owner may have changed the actual selection while a drag was held.
    public int ReleaseTarget(int index) => IsEnabled(index) ? index : SelectedIndex;

    public int NearestEnabled(int index)
    {
        index = Math.Clamp(index, 0, Count - 1);
        for (int distance = 0; distance < Count; distance++)
        {
            if (IsEnabled(index + distance)) return index + distance;
            if (IsEnabled(index - distance)) return index - distance;
        }
        return SelectedIndex;
    }
    public int NextEnabled(int direction)
    {
        for (int step = 1; step <= Count; step++)
        {
            int index = (SelectedIndex + Math.Sign(direction) * step + Count) % Count;
            if (IsEnabled(index)) return index;
        }
        return SelectedIndex;
    }
}
