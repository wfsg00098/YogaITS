namespace YogaITS.Tray.Services;

// Native and glass are two distributions of one controller, sharing one session.
internal static class AppSession
{
    public static bool TryForwardExistingInstance(string[] arguments)
    {
        Mutex mutex;
        try { mutex = Mutex.OpenExisting(@"Local\YogaITS.Tray.Singleton"); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        using (mutex)
        {
            bool free;
            try { free = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { free = true; }
            if (free) { mutex.ReleaseMutex(); return false; }
            if (AppLaunchPolicy.ShouldOpenControlCenter(arguments)) Signal(@"Local\YogaITS.Tray.OpenControlCenter");
            if (AppLaunchPolicy.ShouldOpenMiniWindow(arguments)) Signal(@"Local\YogaITS.Tray.OpenMiniWindow");
            return true;
        }
    }

    private static void Signal(string name)
    {
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        signal.Set();
    }
}
