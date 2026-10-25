namespace YogaITS.Tray.Services.Color;

internal static class CalibrationDownloadPolicy
{
    public const int Redownloads = 3;

    public static async Task<FactoryValidationResult> ValidateAsync(
        Func<int, Task<FactoryValidationResult>> download, CancellationToken cancellationToken)
    {
        string? fingerprint = null;
        bool unchanged = true;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await download(attempt).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Ok || result.CorruptProfiles is not { Count: > 0 }) return result;
            fingerprint ??= result.Fingerprint;
            unchanged &= result.Fingerprint.Length > 0 && result.Fingerprint == fingerprint;
            if (attempt < Redownloads) continue;
            if (unchanged && result.Profiles.Count > 0)
                return result with
                {
                    Ok = true,
                    Message = $"Redownloaded {Redownloads} times; skipped {result.CorruptProfiles.Count} consistently corrupt file(s)."
                };
            return result;
        }
    }
}
