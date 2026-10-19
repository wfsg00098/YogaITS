using System.IO.Compression;
using YogaITS.Tray.Services.Updates;

namespace YogaITS.Tray.Services;

internal static class BoundedFileTransfer
{
    public static async Task<long> CopyAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(20));
            int count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (count == 0) return total;
            total += count;
            if (total > maximum) throw new InvalidDataException("下载内容超过允许大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    public static void ExtractZip(string archive, string directory, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 2048 || zip.Entries.Sum(entry => entry.Length) > 256L * 1024 * 1024)
            throw new InvalidDataException("校色包展开大小或文件数量异常。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        byte[] buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = UpdatePackage.SafeEntryPath(directory, entry.FullName.Replace('\\', '/'));
            if (!paths.Add(path)) throw new InvalidDataException("校色包包含重复路径。");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long fileBytes = 0;
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                fileBytes += count;
                expanded += count;
                if (fileBytes > entry.Length || expanded > 256L * 1024 * 1024)
                    throw new InvalidDataException("校色包实际展开内容超过允许大小。");
                output.Write(buffer, 0, count);
            }
        }
    }
}
