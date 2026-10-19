using System.Diagnostics;

namespace YogaITS.Tray.Services;

internal static class ReadbackPolicy
{
    public static async Task<bool> VerifyAsync(Func<bool> read, CancellationToken token = default)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            if (read()) return true;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(2)) return false;
            await Task.Delay(150, token).ConfigureAwait(false);
        } while (true);
    }
}
