namespace YogaITS.Tray.Services;

internal static class BatteryDriverAccess
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static T Read<T>(Func<T> read)
    {
        Gate.Wait();
        try { return read(); }
        finally { Gate.Release(); }
    }

    public static Task<T> WriteAsync<T>(Func<Task<T>> write) => Task.Run(async () =>
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { return await write().ConfigureAwait(false); }
        finally { Gate.Release(); }
    });
}
