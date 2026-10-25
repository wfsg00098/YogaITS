namespace YogaITS.Tray.UI.Glass.Runtime;

// Production diagnostics stay in Trace; never persist desktop capture logs.
internal static class CaptureDiagnostics
{
    public static void Write(string message)
    {
        System.Diagnostics.Trace.WriteLine("[UI] " + message);
    }
}
