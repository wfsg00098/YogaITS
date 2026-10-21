namespace YogaITS.Tray.Services.KeyboardBacklight;

internal sealed class KeyboardBacklightService : SerializedFeatureService
{
    private readonly KeyboardBacklightBackend _backend;
    public KeyboardBacklightSnapshot Snapshot { get; private set; } = KeyboardBacklightSnapshot.Unavailable;

    public KeyboardBacklightService(KeyboardBacklightBackend? backend = null) =>
        _backend = backend ?? new KeyboardBacklightBackend();

    public Task RefreshAsync() => RefreshAsyncCore(() =>
    {
        try { Snapshot = _backend.Read(); }
        catch (Exception ex)
        {
            Snapshot = KeyboardBacklightSnapshot.Unavailable;
            System.Diagnostics.Debug.WriteLine("Keyboard backlight read failed: " + ex.Message);
        }
        return Task.CompletedTask;
    });

    public Task SetAsync(int state) => RunAsync(() =>
    {
        try { Snapshot = _backend.Set(state); }
        catch
        {
            // Do not publish the requested state or keep stale capabilities visible.
            Snapshot = KeyboardBacklightSnapshot.Unavailable;
            throw;
        }
        return Task.CompletedTask;
    });
}
