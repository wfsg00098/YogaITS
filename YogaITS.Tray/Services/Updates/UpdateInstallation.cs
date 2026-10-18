namespace YogaITS.Tray.Services.Updates;

internal static class UpdateInstallation
{
    public static void Apply(string sourceDirectory, string targetDirectory, Action restart)
    {
        string source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            target.Length <= (Path.GetPathRoot(target)?.Length ?? 0) ||
            !File.Exists(Path.Combine(target, "YogaITS.Tray.exe")))
            throw new InvalidDataException("更新目标不是现有的 YogaITS 程序目录。");
        string backup = Path.Combine(Path.GetDirectoryName(source)!, "backup");
        Directory.CreateDirectory(backup);
        var changed = new List<(string Destination, string? Backup)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                string destination = UpdatePackage.SafeEntryPath(target, relative);
                string? saved = null;
                if (File.Exists(destination))
                {
                    saved = UpdatePackage.SafeEntryPath(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved, overwrite: false);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                changed.Add((destination, saved));
                File.Copy(file, destination, overwrite: true);
                using var input = File.OpenRead(file);
                using var output = File.OpenRead(destination);
                if (!System.Security.Cryptography.SHA256.HashData(input).AsSpan()
                    .SequenceEqual(System.Security.Cryptography.SHA256.HashData(output)))
                    throw new IOException("更新后文件校验失败。");
            }
            restart();
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Backup is not null)
                    {
                        // A sharing violation may occur before any bytes were written.
                        // Do not report rollback failure for an already intact locked file.
                        if (File.Exists(item.Destination) &&
                            File.ReadAllBytes(item.Backup).AsSpan().SequenceEqual(File.ReadAllBytes(item.Destination))) continue;
                        File.Copy(item.Backup, item.Destination, overwrite: true);
                    }
                    else File.Delete(item.Destination);
                }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            if (errors.Count > 1) throw new AggregateException("更新失败，部分文件未能恢复；旧文件保留在 " + backup, errors);
            throw;
        }
    }
}
