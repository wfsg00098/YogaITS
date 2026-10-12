namespace YogaITS.Tray.Services;

internal static class UiErrorLog
{
    private static readonly object Gate = new();

    public static void Write(string operation, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "ui-errors.log"),
                    $"{DateTimeOffset.Now:O} {operation} (0x{exception.HResult:X8}){Environment.NewLine}{exception}{Environment.NewLine}");
            }
        }
        catch { /* Diagnostics must never replace the original error. */ }
    }
}
