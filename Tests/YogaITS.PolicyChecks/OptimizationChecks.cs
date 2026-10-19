using System.IO.Compression;
using YogaITS.Tray.Services;
using YogaITS.Tray.Services.Updates;

internal static class OptimizationChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAILED optimization check: " + message);
            checks++;
        }
        var queue = new AsyncOperationQueue();
        var revisions = new SnapshotRevisions(6);
        var observed = revisions.Capture();
        revisions.Changed(0, 1);
        Check(!revisions.IsCurrent(0, observed) && !revisions.IsCurrent(1, observed) &&
            revisions.IsCurrent(2, observed) && revisions.IsCurrent(5, observed),
            "a write rejects only its own stale readback, not unrelated refresh results");
        observed = revisions.Capture();
        Check(Enumerable.Range(0, 6).All(field => revisions.IsCurrent(field, observed)),
            "a new batch captures the current per-feature revisions");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        Task first = queue.Enqueue(async () => { entered.SetResult(); await release.Task; order.Add("first"); }, refresh: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task pending = queue.Enqueue(() => { order.Add("refresh"); return Task.CompletedTask; }, refresh: true);
        foreach (int _ in Enumerable.Range(0, 100))
            Check(ReferenceEquals(pending, queue.Enqueue(() => throw new Exception("must be coalesced"), refresh: true)), "refresh bursts share one pending read");
        Task write = queue.Enqueue(() => { order.Add("write"); return Task.CompletedTask; });
        release.SetResult();
        await Task.WhenAll(first, pending, write).WaitAsync(TimeSpan.FromSeconds(5));
        Check(order.SequenceEqual(new[] { "first", "write", "refresh" }), "writes overtake queued refreshes without overlapping a live read");
        try { await queue.Enqueue(() => throw new IOException("fixture")); } catch (IOException) { }
        bool recovered = false;
        await queue.Enqueue(() => { recovered = true; return Task.CompletedTask; });
        Check(recovered, "operation failure does not poison the queue");
        await using (var actions = new SerializedActionService())
            Check(await actions.ExecuteAsync(() => Task.FromResult(42)) == 42,
                "core control action wrapper returns the confirmed result through the same serialized executor");
        await using (var service = new TimedService())
        {
            var nativeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task read = service.Read(async () => { nativeEntered.SetResult(); await nativeContinue.Task; });
            try
            {
                await nativeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await read.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!service.IsResponsive, "service timeout disables UI actions without disposing a live native call");
                bool wrote = false;
                Task later = service.Write(() => { wrote = true; return Task.CompletedTask; });
                Check(!wrote, "timeout retains serialization instead of running a write over the stuck read");
                nativeContinue.SetResult();
                await later.WaitAsync(TimeSpan.FromSeconds(5));
                Check(wrote && service.IsResponsive, "late native completion safely restores the queue and availability");
            }
            finally { nativeContinue.TrySetResult(); }
        }

        await using (var service = new TimedService(shortWrites: true))
        {
            var nativeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task read = service.Read(async () => { nativeEntered.SetResult(); await nativeContinue.Task; });
            try
            {
                await nativeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await read;
                bool wrote = false, expired = false;
                try { await service.Write(() => { wrote = true; return Task.CompletedTask; }); }
                catch (TimeoutException) { expired = true; }
                Check(expired && !wrote, "queued setting expires without issuing a hardware command");
                nativeContinue.SetResult();
                await service.Write(() => Task.CompletedTask);
                Check(!wrote, "expired queued write is skipped even after the native read recovers");
            }
            finally { nativeContinue.TrySetResult(); }
        }

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            bool stopped = false;
            try { await ReadbackPolicy.VerifyAsync(() => throw new Exception("must not read"), canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "canceled verification performs no backend query");
        }
        int attempts = 0;
        Check(await ReadbackPolicy.VerifyAsync(() => ++attempts == 3) && attempts == 3,
            "delayed readback succeeds with bounded polling instead of premature failure");
        attempts = 0;
        Check(!await ReadbackPolicy.VerifyAsync(() => { attempts++; return false; }) && attempts is > 1 and < 30,
            "unconfirmed readback stops after a finite number of attempts");

        var probe = new BoundedProbe<int>();
        using var nativeStarted = new ManualResetEventSlim();
        using var nativeRelease = new ManualResetEventSlim();
        int calls = 0;
        Task<ProbeResult<int>> blocked = probe.ReadAsync(() =>
        {
            Interlocked.Increment(ref calls); nativeStarted.Set(); nativeRelease.Wait(); return 10;
        }, TimeSpan.FromMilliseconds(100));
        Check(nativeStarted.Wait(TimeSpan.FromSeconds(5)), "owned fake native read started");
        Check(!(await blocked).Success, "hung native read has a bounded caller wait");
        Check(!(await probe.ReadAsync(() => { calls++; return 20; })).Success && calls == 1,
            "timeout never submits another read over the still-running native call");
        nativeRelease.Set();
        ProbeResult<int> fresh = default;
        for (int i = 0; i < 50 && !fresh.Success; i++)
        {
            await Task.Delay(10);
            fresh = await probe.ReadAsync(() => { calls++; return 20; });
        }
        Check(fresh.Success && fresh.Value == 20 && calls == 2, "late stale native result is discarded before a fresh read resumes");
        var batteryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batteryContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> batteryWrite = BatteryDriverAccess.WriteAsync(async () =>
        {
            batteryEntered.SetResult(); await batteryContinue.Task; return 7;
        });
        await batteryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> batteryRead = Task.Run(() => BatteryDriverAccess.Read(() => 8));
        await Task.Delay(20);
        Check(!batteryRead.IsCompleted, "battery read cannot overlap an in-flight two-command mode write");
        batteryContinue.SetResult();
        Check(await batteryWrite == 7 && await batteryRead == 8, "battery read resumes only after the write releases the shared gate");

        foreach (string name in new[] { "dwm", "DWM.EXE", "explorer", "Explorer.exe", "YogaITS.dGPUKeepAwake" })
            Check(NvidiaGpuHelper.IsProtectedProcessName(name), "execution-time protected process check: " + name);
        Check(!NvidiaGpuHelper.IsProtectedProcessName("nvcontainer.exe"), "NVIDIA containers remain eligible; no broad whitelist was added");
        Check(NvidiaGpuHelper.SameProcess(123, 123) && !NvidiaGpuHelper.SameProcess(123, 124) &&
            !NvidiaGpuHelper.SameProcess(null, 123) && !NvidiaGpuHelper.SameProcess(0, 0),
            "only a positive matching birth time authorizes killing a captured PID");

        using (var input = new MemoryStream(new byte[9]))
        using (var output = new MemoryStream())
            Check(await BoundedFileTransfer.CopyAsync(input, output, 9, default) == 9 && output.Length == 9,
                "bounded calibration transfer accepts its exact limit");
        using (var input = new MemoryStream(new byte[10]))
        using (var output = new MemoryStream())
        {
            bool rejected = false;
            try { await BoundedFileTransfer.CopyAsync(input, output, 9, default); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && output.Length == 0, "oversized calibration chunk is rejected before writing");
        }

        string root = Path.Combine(Path.GetTempPath(), "YogaITS-OptimizationChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string MakeStage(bool completed, bool old, string? name = null)
            {
                string path = Path.Combine(root, name ?? Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                if (completed)
                {
                    string marker = Path.Combine(path, "completed");
                    File.WriteAllText(marker, "YogaITS.Update.Completed.v1");
                    if (old) File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-8));
                }
                return path;
            }
            string expired = MakeStage(true, true), recent = MakeStage(true, false), failed = MakeStage(false, true);
            string unknown = MakeStage(true, true, "user-files");
            UpdateStaging.RemoveExpiredCompleted(root, DateTime.UtcNow);
            Check(!Directory.Exists(expired), "only expired confirmed-success update staging is removed");
            Check(Directory.Exists(recent) && Directory.Exists(failed) && Directory.Exists(unknown),
                "recent, failed/recovery and unrecognized directories are preserved");
            string archive = Path.Combine(root, "unsafe.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry("../escape.icm");
            bool rejected = false;
            try { BoundedFileTransfer.ExtractZip(archive, Path.Combine(root, "extract"), default); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "escape.icm")), "calibration extraction rejects traversal");
            Check(UpdatePackage.RequiredFiles.Contains("YogaITS.Updater.runtimeconfig.json") &&
                UpdatePackage.RequiredFiles.Contains("YogaITS.dGPUKeepAwake.exe"), "complete update manifest includes both helpers");
            foreach (string missing in UpdatePackage.RequiredFiles)
            {
                string incomplete = Path.Combine(root, "incomplete.zip");
                if (File.Exists(incomplete)) File.Delete(incomplete);
                using (var zip = ZipFile.Open(incomplete, ZipArchiveMode.Create))
                    foreach (string name in UpdatePackage.RequiredFiles.Where(name => name != missing))
                        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("fixture");
                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(incomplete)));
                bool incompleteRejected = false;
                try { UpdatePackage.ExtractAndVerify(incomplete, Path.Combine(root, "missing-" + missing), digest); }
                catch (InvalidDataException) { incompleteRejected = true; }
                Check(incompleteRejected, "incomplete update rejected before installation: " + missing);
            }
        }
        finally { Directory.Delete(root, recursive: true); } // Only this test's unique fixture directory.
        return checks;
    }

    private sealed class TimedService(bool shortWrites = false) : SerializedFeatureService
    {
        protected override TimeSpan ReadTimeout => TimeSpan.FromMilliseconds(100);
        protected override TimeSpan WriteTimeout => shortWrites ? TimeSpan.FromMilliseconds(100) : base.WriteTimeout;
        public Task Read(Func<Task> operation) => RefreshAsyncCore(operation);
        public Task Write(Func<Task> operation) => RunAsync(operation);
    }
}
