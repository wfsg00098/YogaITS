namespace YogaITS.Tray.UI;

internal static class OptionSetPolicy
{
    // The owner must confirm a surviving mode before its selected entry can be
    // removed. Remap by identity so inserting/reordering does not change modes.
    public static int RemapConfirmed(IReadOnlyList<string> previous, int selected, IReadOnlyList<string> next)
    {
        if (next.Count < 1 || next.Distinct(StringComparer.Ordinal).Count() != next.Count)
            throw new ArgumentException("An option set needs unique labels.", nameof(next));
        if (selected < 0 || selected >= previous.Count) throw new ArgumentOutOfRangeException(nameof(selected));
        for (int i = 0; i < next.Count; i++)
            if (string.Equals(previous[selected], next[i], StringComparison.Ordinal)) return i;
        throw new InvalidOperationException("Confirm an available option before removing the current selection.");
    }
}
