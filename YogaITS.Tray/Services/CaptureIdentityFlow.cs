using System.Text;

namespace YogaITS.Tray.Services;

// True continues this process; false means one replacement was started.
// Test orchestration without changing package registrations or opening windows.
internal static class CaptureIdentityFlow
{
    public static bool ContinueStartup(bool matchingIdentity, bool restarted,
        Func<bool> prepare, Action handoff, Action<Exception> failed)
    {
        if (matchingIdentity || restarted) return true;
        try
        {
            if (!prepare()) return true;
            handoff();
            return false;
        }
        catch (Exception ex) { failed(ex); return true; }
    }

    // Windows argv quoting: this is data for CreateProcess, never shell text.
    public static string CommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));
    private static string Quote(string argument)
    {
        var result = new StringBuilder().Append('"');
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static bool SameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
