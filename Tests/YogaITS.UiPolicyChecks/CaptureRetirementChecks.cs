using System.Collections.Concurrent;
using YogaITS.Tray.UI;

internal static class CaptureRetirementChecks
{
    public static int Run()
    {
        int checks=0;
        void Check(bool value,string message) { checks++; if(!value) throw new InvalidOperationException("Capture retirement: "+message); }
        var queue=new ConcurrentQueue<Action>();
        var operations=new CaptureOperationState();
        int fallbacks=0;
        long original=operations.Begin();
        using(var entered=new ManualResetEventSlim())
        using(var resume=new ManualResetEventSlim())
        {
            var worker=new LatestWorkLoop<string>(_=>
            {
                entered.Set();
                if(!resume.Wait(5000)) throw new TimeoutException();
            },_=>queue.Enqueue(()=>operations.RunIfCurrent(original,()=>fallbacks++)),
                cleanup:()=>throw new IOException("synthetic retired cleanup failure"));
            try
            {
                worker.Post("in-flight"); Check(entered.Wait(5000),"Old capture is in flight");
                operations.Retire(); worker.Dispose();
                Check(!worker.Completion.IsCompleted,"Switching away does not wait for capture cleanup");
                long current=operations.Begin(); // Even after entering a new transparent session.
                resume.Set(); Check(worker.Completion.Wait(5000),"Retired capture cleanup completes");
                while(queue.TryDequeue(out var action)) action();
                Check(fallbacks==0,"A retired cleanup error cannot force the new background into native UI");
                operations.RunIfCurrent(current,()=>fallbacks++);
                Check(fallbacks==1,"A genuine error from the current source is still reported");
                operations.Retire(); operations.RunIfCurrent(current,()=>fallbacks++);
                Check(fallbacks==1,"Static/wallpaper mode rejects late capture errors");
            }
            finally { resume.Set(); worker.Dispose(); worker.Completion.Wait(5000); }
        }
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int released=0;
        var release=OwnerThreadRelease.AfterAsync(stopped.Task,action=>{queue.Enqueue(action);return true;},()=>released++,_=>throw new InvalidOperationException());
        Check(!release.IsCompleted && released==0,"Owner resources survive an in-flight draw without blocking UI");
        stopped.SetResult();
        Check(SpinWait.SpinUntil(()=>!queue.IsEmpty,5000),"Owner cleanup is dispatched only after worker completion");
        Check(released==0,"Worker never closes owner resources directly");
        while(queue.TryDequeue(out var action)) action();
        Check(release.Wait(5000) && release.Result && released==1,"Owner resources close once on the dispatched owner");
        var unavailable=OwnerThreadRelease.AfterAsync(Task.CompletedTask,_=>false,()=>released++,_=>{});
        Check(unavailable.Wait(5000) && !unavailable.Result && released==1,"A stopped owner queue never triggers foreign-thread Close");
        return checks;
    }
}
