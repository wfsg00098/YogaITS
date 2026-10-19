namespace YogaITS.Tray.Services.Updates;

internal static class UpdateStaging
{
    public static void RemoveExpiredCompleted(string root, DateTime nowUtc)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                // Never delete unknown/failed staging, active installs, or links.
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string marker = Path.Combine(directory, "completed");
                if (!File.Exists(marker) || File.ReadAllText(marker) != "YogaITS.Update.Completed.v1" ||
                    nowUtc - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(7)) continue;
                if (ContainsLinks(directory)) continue;
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool ContainsLinks(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0 && ContainsLinks(path)) return true;
        }
        return false;
    }
}
