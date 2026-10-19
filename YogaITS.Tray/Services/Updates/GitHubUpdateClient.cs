using System.Diagnostics;

namespace YogaITS.Tray.Services.Updates;

internal sealed class GitHubUpdateClient : IDisposable
{
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        var version = typeof(GitHubUpdateClient).Assembly.GetName().Version ?? new Version(1, 0);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"YogaITS-Updater/{version.Major}.{version.Minor}");
    }

    public async Task<UpdateRelease> GetLatestAsync(CancellationToken cancellationToken)
    {
        return await RaceAsync(async (proxy, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            Uri url = proxy ? UpdatePolicy.ViaProxy(UpdatePolicy.LatestReleaseUrl) : UpdatePolicy.LatestReleaseUrl;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
            return UpdatePolicy.ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token));
        }, cancellationToken);
    }

    public async Task DownloadAsync(UpdateRelease release, string path, CancellationToken cancellationToken)
    {
        string directPath = path + "." + Guid.NewGuid().ToString("N") + ".direct.part";
        string proxyPath = path + "." + Guid.NewGuid().ToString("N") + ".proxy.part";
        try
        {
            string winner = await RaceAsync((proxy, token) => DownloadRouteAsync(release,
                proxy ? UpdatePolicy.ViaProxy(release.DownloadUrl) : release.DownloadUrl,
                proxy ? proxyPath : directPath, token), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Both requests have closed their handles. Never publish partial or
            // unverified bytes as the archive consumed by the installer.
            File.Move(winner, path, overwrite: true);
        }
        finally
        {
            foreach (string temporary in new[] { directPath, proxyPath })
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Debug.WriteLine("Update partial cleanup unavailable: " + ex.GetType().Name); }
            }
        }
    }

    private async Task<string> DownloadRouteAsync(UpdateRelease release, Uri url, string path, CancellationToken token)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(TimeSpan.FromMinutes(8));
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("更新包大小与发布信息不一致。");
        await using var source = await response.Content.ReadAsStreamAsync(total.Token);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        byte[] buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(20));
            int count = await source.ReadAsync(buffer, idle.Token);
            if (count == 0) break;
            received += count;
            if (received > release.Size) throw new InvalidDataException("更新包超过预期大小。");
            await destination.WriteAsync(buffer.AsMemory(0, count), total.Token);
        }
        if (received != release.Size) throw new InvalidDataException("更新包未下载完整。");
        await destination.FlushAsync(total.Token);
        await destination.DisposeAsync();
        token.ThrowIfCancellationRequested();
        UpdatePackage.VerifyArchive(path, release.Sha256);
        token.ThrowIfCancellationRequested();
        return path;
    }

    private static async Task<T> RaceAsync<T>(Func<bool, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Wrap synchronous failures too, so both routes always start.
        async Task<T> StartAsync(bool proxy)
        {
            race.Token.ThrowIfCancellationRequested();
            return await action(proxy, race.Token).ConfigureAwait(false);
        }
        Task<T>[] requests = [StartAsync(false), StartAsync(true)];
        var pending = requests.ToList();
        var failures = new List<Exception>();
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                pending.Remove(finished);
                try
                {
                    T result = await finished.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return result;
                }
                catch (Exception ex) when (!token.IsCancellationRequested &&
                    ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException
                        or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {
                    failures.Add(ex);
                }
            }
            throw new HttpRequestException("GitHub 和代理站的更新请求均未成功，请稍后重试。", new AggregateException(failures));
        }
        finally
        {
            // Stop the loser immediately, then drain it before disposing tokens
            // or removing its partial file. No background download survives us.
            race.Cancel();
            try { await Task.WhenAll(requests).ConfigureAwait(false); }
            catch (Exception) { /* Observe both tasks without masking the winning result/original error. */ }
        }
    }

    public void Dispose() => _http.Dispose();
}
