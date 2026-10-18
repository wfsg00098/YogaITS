using System.Diagnostics;

namespace YogaITS.Tray.Services.Updates;

internal sealed class GitHubUpdateClient : IDisposable
{
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("YogaITS-Updater/5.0");
    }

    public async Task<UpdateRelease> GetLatestAsync(CancellationToken cancellationToken)
    {
        return await WithFallbackAsync(async (proxy, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(proxy ? 20 : 8));
            Uri url = proxy ? UpdatePolicy.ViaProxy(UpdatePolicy.LatestReleaseUrl) : UpdatePolicy.LatestReleaseUrl;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
            return UpdatePolicy.ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token));
        }, cancellationToken);
    }

    public async Task DownloadAsync(UpdateRelease release, string path, CancellationToken cancellationToken)
    {
        await WithFallbackAsync(async (proxy, token) =>
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
            total.CancelAfter(TimeSpan.FromMinutes(proxy ? 8 : 3));
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(proxy ? 20 : 8));
            Uri url = proxy ? UpdatePolicy.ViaProxy(release.DownloadUrl) : release.DownloadUrl;
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("更新包大小与发布信息不一致。");
            await using var source = await response.Content.ReadAsStreamAsync(total.Token);
            await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920];
            long received = 0;
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(proxy ? 20 : 10));
                int count = await source.ReadAsync(buffer, idle.Token);
                if (count == 0) break;
                received += count;
                if (received > release.Size) throw new InvalidDataException("更新包超过预期大小。");
                await destination.WriteAsync(buffer.AsMemory(0, count), total.Token);
                if (!proxy && elapsed.Elapsed.TotalSeconds >= 10 && received / elapsed.Elapsed.TotalSeconds < 32768)
                    throw new IOException("GitHub 下载速度过低，切换加速线路。");
            }
            if (received != release.Size) throw new InvalidDataException("更新包未下载完整。");
            await destination.FlushAsync(total.Token);
            await destination.DisposeAsync();
            UpdatePackage.VerifyArchive(path, release.Sha256);
            return true;
        }, cancellationToken);
    }

    private static async Task<T> WithFallbackAsync<T>(Func<bool, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try { return await action(false, token); }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException
                or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            return await action(true, token);
        }
    }

    public void Dispose() => _http.Dispose();
}
