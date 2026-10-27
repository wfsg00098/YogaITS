using System.ComponentModel;

namespace YogaITS.Tray.Services.AlwaysOnUsb;

internal sealed class AlwaysOnUsbService : SerializedFeatureService
{
    private readonly AlwaysOnUsbBackend _backend;
    public AlwaysOnUsbSnapshot Snapshot { get; private set; } = AlwaysOnUsbSnapshot.Unavailable;

    public AlwaysOnUsbService(AlwaysOnUsbBackend? backend = null) => _backend = backend ?? new();

    public Task RefreshAsync() => RefreshAsyncCore(() =>
    {
        try { Snapshot = new(true, true, _backend.Read()); }
        catch (Exception ex)
        {
            ReadFailed(ex);
            System.Diagnostics.Debug.WriteLine("Always-On USB read failed: " + ex.Message);
        }
        return Task.CompletedTask;
    });

    public Task SetAsync(AlwaysOnUsbMode mode) => RunAsync(async () =>
    {
        (bool Confirmed, AlwaysOnUsbMode Actual) result;
        try { result = await _backend.SetAsync(mode).ConfigureAwait(false); }
        catch (Exception ex)
        {
            ReadFailed(ex);
            throw;
        }
        Snapshot = new(true, true, result.Actual);
        if (!result.Confirmed)
            throw new InvalidOperationException("Always-On USB 设置未获回读确认，已显示实际状态，请稍后重试。");
    });

    private void ReadFailed(Exception error)
    {
        // Preserve confirmed capability during transient failures; a missing device hides it.
        Snapshot = error is Win32Exception { NativeErrorCode: 1 or 2 or 3 or 50 or 55 or 1167 }
            ? AlwaysOnUsbSnapshot.Unavailable : Snapshot with { Available = false };
    }
}
