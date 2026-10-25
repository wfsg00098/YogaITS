namespace YogaITS.Tray.UI.Glass.Capture;

// Let in-flight work finish, then close thread-affine resources on their owner.
// Neither the UI nor the worker waits synchronously for the other thread.
internal static class OwnerThreadRelease
{
    public static async Task<bool> AfterAsync(Task stopped, Func<Action,bool> post, Action release, Action<Exception> failed)
    {
        void Report(Exception error) { try { failed(error); } catch { } }
        try { await stopped.ConfigureAwait(false); }
        catch(Exception ex) { Report(ex); }
        var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if(!post(()=>
            {
                try { release(); done.TrySetResult(true); }
                catch(Exception ex) { Report(ex); done.TrySetResult(false); }
            })) return false; // The owner queue has shut down; never Close from a foreign thread.
        }
        catch(Exception ex) { Report(ex); return false; }
        return await done.Task.ConfigureAwait(false);
    }
}
