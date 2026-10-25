namespace YogaITS.Tray.Services;

// Import to a new directory so the renderer can finish reading the previous
// image. Settings reference only the completed copy, never the original file.
internal sealed class BackgroundImageStore
{
    private readonly string _directory;

    public BackgroundImageStore(string directory) =>
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    public bool Contains(string path) => Path.GetFullPath(path).StartsWith(
        _directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public async Task<string> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(path);
        if (Path.GetExtension(source).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff" or ".webp"))
            throw new InvalidDataException("请选择受支持的图像文件。");
        if (Contains(source) && File.Exists(source)) return source;

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string importDirectory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(importDirectory, Path.GetFileName(source));
        string temporary = Path.Combine(importDirectory, "image.tmp");
        Directory.CreateDirectory(importDirectory);
        bool completed = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            completed = true;
            return destination;
        }
        finally
        {
            if (!completed) Directory.Delete(importDirectory, recursive: true);
        }
    }
}
