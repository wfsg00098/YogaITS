namespace YogaITS.Tray.Services.Color;

internal static class ColorDiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Last = new();

    public static void Write(string operation, IReadOnlyList<string> lines)
    {
        try
        {
            string detail = string.Join(Environment.NewLine, lines);
            lock (Gate)
            {
                if (Last.GetValueOrDefault(operation) == detail) return;
                string directory = Path.Combine(AppContext.BaseDirectory, "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "color-diagnostics.log"),
                    $"{DateTimeOffset.Now:O} {operation}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
                Last[operation] = detail;
            }
        }
        catch { /* Diagnostics must never change hardware-operation results. */ }
    }
}
