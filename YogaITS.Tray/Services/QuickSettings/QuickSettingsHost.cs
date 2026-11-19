using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace YogaITS.Tray.Services.QuickSettings;

internal enum IconPlacement { Tray, QuickSettings }

// The shell can report availability and invoke only the two window commands.
// Named events use the current session and Windows' default token ACL.
internal sealed class QuickSettingsHost : IDisposable
{
    internal const string Component = "YogaITS.QuickSettings.dll";
    private const string Prefix = @"Local\YogaITS.QuickSettings.";
    private static readonly Guid Tap = new("a3b07347-a74a-4486-a397-2d71594b965a");
    private readonly EventWaitHandle _owner = Create("Owner", EventResetMode.ManualReset);
    private readonly EventWaitHandle _enabled = Create("Enabled", EventResetMode.ManualReset);
    private readonly EventWaitHandle _ready = Create("Ready", EventResetMode.ManualReset);
    private readonly EventWaitHandle _left = Create("Left", EventResetMode.AutoReset);
    private readonly EventWaitHandle _right = Create("Right", EventResetMode.AutoReset);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Action<bool> _availability;
    private readonly Action<bool> _click;
    private int _revision;

    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "ShellHost.exe")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, Component));

    public QuickSettingsHost(Action<bool> availability, Action<bool> click)
    {
        _availability = availability;
        _click = click;
        _owner.Set();
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _left.Reset();
            _right.Reset();
            _enabled.Set();
        }
        else
        {
            _enabled.Reset();
            _ready.Reset();
        }
        Interlocked.Increment(ref _revision);
    }

    private static EventWaitHandle Create(string suffix, EventResetMode mode) => new(false, mode, Prefix + suffix);

    private void Run()
    {
        bool published = false, attached = false;
        int publishedRevision = 0;
        int shellPid = 0;
        long nextProbe = 0, nextAttach = 0;
        string? runtime = null;
        WaitHandle[] wait = [_stop.Token.WaitHandle, _left, _right];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int signal = WaitHandle.WaitAny(wait, 200);
                if (signal == 0) break;
                int revision = Volatile.Read(ref _revision);
                bool enabled = _enabled.WaitOne(0);
                if (enabled && Environment.TickCount64 >= nextProbe)
                {
                    int current = FindShell();
                    if (current != shellPid)
                    {
                        _ready.Reset();
                        shellPid = current;
                        attached = false;
                        nextAttach = 0;
                    }
                    nextProbe = Environment.TickCount64 + 1500;
                    if (shellPid != 0 && !attached && Environment.TickCount64 >= nextAttach)
                    {
                        try
                        {
                            runtime ??= StageComponent();
                            attached = Attach(shellPid, runtime);
                        }
                        catch (Exception ex) { Debug.WriteLine("Quick Settings connection: " + ex.Message); }
                        nextAttach = Environment.TickCount64 + 15000;
                    }
                }

                if (revision != Volatile.Read(ref _revision)) continue;
                bool ready = enabled && shellPid != 0 && _ready.WaitOne(0);
                if (published != ready || publishedRevision != revision)
                {
                    publishedRevision = revision;
                    _availability(published = ready);
                }
                if (ready && signal is 1 or 2) _click(signal == 2);
            }
        }
        catch (Exception ex) { Debug.WriteLine("Quick Settings bridge: " + ex.Message); }
        finally
        {
            if (published) _availability(false);
            _owner.Dispose();
            _enabled.Dispose();
            _ready.Dispose();
            _left.Dispose();
            _right.Dispose();
        }
    }

    private static int FindShell()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)) return 0;
        string expected = Path.Combine(Environment.SystemDirectory, "ShellHost.exe");
        foreach (var process in Process.GetProcessesByName("ShellHost"))
        {
            using (process)
            {
                int id = process.Id;
                if (!ProcessIdToSessionId((uint)id, out uint candidate) || candidate != session) continue;
                nint handle = OpenProcess(0x1000, false, (uint)id);
                if (handle == 0) continue;
                try
                {
                    var path = new StringBuilder(1024);
                    int length = path.Capacity;
                    if (QueryFullProcessImageName(handle, 0, path, ref length) &&
                        string.Equals(path.ToString(), expected, StringComparison.OrdinalIgnoreCase)) return id;
                }
                finally { CloseHandle(handle); }
            }
        }
        return 0;
    }

    private static string StageComponent()
    {
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, Component));
        string folder = Path.Combine(UserSettingsStore.DataDirectory, "QuickSettings");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(image)) + ".dll");
        if (!File.Exists(path))
        {
            string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, image);
                File.Move(temporary, path);
            }
            finally { File.Delete(temporary); }
        }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(image))
            throw new IOException("快速设置组件副本校验失败。");
        return path;
    }

    private bool Attach(int pid, string path)
    {
        nint library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "Windows.UI.Xaml.dll"));
        try
        {
            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeDiagnostics>(
                NativeLibrary.GetExport(library, "InitializeXamlDiagnosticsEx"));
            const int notFound = unchecked((int)0x80070490);
            for (int index = 1; index <= 10_000 && !_stop.IsCancellationRequested && _enabled.WaitOne(0); index++)
            {
                int result = initialize($"VisualDiagConnection{index}", (uint)pid, "", path, Tap, null);
                if (result == notFound) continue;
                Debug.WriteLine($"Quick Settings attachment to ShellHost {pid}: 0x{result:X8}");
                return result >= 0;
            }
            return false;
        }
        finally { NativeLibrary.Free(library); }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _enabled.Reset();
        _owner.Reset();
        _stop.Cancel();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int InitializeDiagnostics(string endpoint, uint pid, string diagnostics, string tap, Guid clsid, string? data);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
