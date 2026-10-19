namespace YogaITS.Tray.Services;

// Owned by the cache's lock. A mutation invalidates only that feature's pending
// read, not unrelated battery/startup/model results from the same refresh batch.
internal sealed class SnapshotRevisions(int fields)
{
    private readonly long[] _values = new long[fields];
    public long[] Capture() => (long[])_values.Clone();
    public bool IsCurrent(int field, long[] observed) => _values[field] == observed[field];
    public void Changed(params int[] fields)
    {
        foreach (int field in fields) _values[field]++;
    }
}
