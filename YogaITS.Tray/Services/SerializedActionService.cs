namespace YogaITS.Tray.Services;

// Core controls share the same bounded, ordered execution as the feature panels.
// Cache publication stays inside the operation, including a late native result.
internal sealed class SerializedActionService : SerializedFeatureService
{
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        T result = default!;
        await RunAsync(async () => result = await action().ConfigureAwait(false));
        return result;
    }
}
