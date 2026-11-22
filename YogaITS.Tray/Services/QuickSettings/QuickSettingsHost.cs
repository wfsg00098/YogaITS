using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace YogaITS.Tray.Services.QuickSettings;

internal enum IconPlacement { Tray, QuickSettings }

// The shell signals mount changes and the two window commands within the current session.
internal sealed class QuickSettingsHost : IDisposable
{
    internal const string Component = "YogaITS.QuickSettings.dll";
    internal const string OwnerWindowTitle = "YogaITS.QuickSettings.Owner";
    private const string Prefix = @"Local\YogaITS.QuickSettings.";
    private const string ControllerProperty = "YogaITS.QuickSettings.Controller";
    private const string ContextProperty = "YogaITS.QuickSettings.Context";
    private const string MountedProperty = "YogaITS.QuickSettings.Mounted";
    private const int ProbeInterval = 2_000;
    private const int OpenCheckDelay = 180;
    private const int RecoveryCheckInterval = 300;
    private const uint RefreshMessage = 0x8001;
    private const uint QueryStateMessage = 0x8002;
    private const uint RecycleMessage = 0x8003;
    private const string PanelClass = "ControlCenterWindow";
    private static readonly Guid Tap = new("a3b07347-a74a-4486-a397-2d71594b965a");
    private readonly nint _controller;
    private readonly EventWaitHandle _owner = Create("Owner", EventResetMode.ManualReset);
    private readonly EventWaitHandle _enabled = Create("Enabled", EventResetMode.ManualReset);
    private readonly EventWaitHandle _mountChanged = Create("Ready", EventResetMode.AutoReset);
    private readonly EventWaitHandle _left = Create("Left", EventResetMode.AutoReset);
    private readonly EventWaitHandle _right = Create("Right", EventResetMode.AutoReset);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly AutoResetEvent _panelShown = new(false);
    private readonly WinEventProc _onPanelShown;
    private readonly nint _showHook;
    private readonly Action<int, bool> _trayFallback;
    private nint _shownPanel;
    private readonly Action<bool> _click;
    private int _revision;

    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "ShellHost.exe")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, Component));

    public int Revision => Volatile.Read(ref _revision);

    // Created and disposed on the main window's message-loop thread.
    public QuickSettingsHost(nint controller, Action<int, bool> trayFallback, Action<bool> click)
    {
        if (!SetProp(controller, ControllerProperty, controller)) throw new Win32Exception();
        _controller = controller;
        _trayFallback = trayFallback;
        const uint eventObjectShow = 0x8002;
        const uint outOfContextSkipOwnProcess = 0x0002;
        _onPanelShown = OnPanelShown;
        _showHook = SetWinEventHook(eventObjectShow, eventObjectShow, 0, _onPanelShown, 0, 0, outOfContextSkipOwnProcess);
        if (_showHook == 0) Trace.WriteLine("Quick Settings show hook unavailable; using the host probe.");
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
        else _enabled.Reset();
        Interlocked.Increment(ref _revision);
        _mountChanged.Set();
    }

    private static EventWaitHandle Create(string suffix, EventResetMode mode) => new(false, mode, Prefix + suffix);

    private void OnPanelShown(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (window == 0 || objectId != 0 || childId != 0 || !_enabled.WaitOne(0)) return;
        var name = new StringBuilder(64);
        if (GetClassName(window, name, name.Capacity) == 0 || name.ToString() != PanelClass) return;
        Volatile.Write(ref _shownPanel, window);
        _panelShown.Set();
    }

    private void Run()
    {
        bool fallback = false, checkingOpening = false;
        int revision = -1, shellPid = 0;
        nint panel = 0;
        long nextProbe = 0, nextCheck = long.MaxValue;
        string? runtime = null;
        var recovery = new QuickSettingsRecovery();
        WaitHandle[] wait = [_stop.Token.WaitHandle, _left, _right, _mountChanged, _panelShown];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int timeout = (int)Math.Clamp(nextCheck - Environment.TickCount64, 1, 200);
                int signal = WaitHandle.WaitAny(wait, timeout);
                if (signal == 0) break;
                if (!_enabled.WaitOne(0))
                {
                    nextCheck = long.MaxValue;
                    continue;
                }
                int currentRevision = Revision;
                if (currentRevision != revision)
                {
                    revision = currentRevision;
                    fallback = false;
                    shellPid = 0;
                    panel = 0;
                    nextProbe = 0;
                    nextCheck = long.MaxValue;
                    recovery = new QuickSettingsRecovery();
                }

                long now = Environment.TickCount64;
                nint shown = signal == 4 ? Volatile.Read(ref _shownPanel) : 0;
                bool probe = now >= nextProbe || (shown != 0 && !IsVisiblePanel(shown, shellPid));
                bool shellChanged = false;
                if (probe)
                {
                    int current = FindShell();
                    shellChanged = current != shellPid;
                    if (shellChanged)
                    {
                        shellPid = current;
                        panel = 0;
                        nextCheck = long.MaxValue;
                        recovery.ResetConnection();
                        Trace.WriteLine($"Quick Settings ShellHost changed to {shellPid}.");
                    }
                    nextProbe = now + ProbeInterval;
                }
                if (shellPid == 0) continue;

                nint previousPanel = panel;
                if (IsVisiblePanel(shown, shellPid)) panel = shown;
                else if (!IsVisiblePanel(panel, shellPid)) panel = probe ? FindVisiblePanel(shellPid) : 0;
                bool opened = panel != 0 && (shown == panel || panel != previousPanel);
                if (panel == 0)
                {
                    nextCheck = long.MaxValue;
                    checkingOpening = false;
                }
                if (!probe && !opened && now < nextCheck && signal is not (1 or 2 or 3)) continue;

                var bridges = FindBridges(shellPid, out bool mounted);
                if (shellChanged && bridges.Count == 0)
                {
                    TryAttach(shellPid, ref runtime);
                    now = Environment.TickCount64;
                    recovery.DelayAfterAttach(now);
                }
                if (opened)
                {
                    Trace.WriteLine($"Quick Settings opened in ShellHost {shellPid} ({(shown == panel ? "show event" : "host probe")}).");
                    foreach (nint bridge in bridges) PostMessage(bridge, RefreshMessage, _controller, 0);
                    nextCheck = now + OpenCheckDelay;
                    checkingOpening = true;
                }
                else if (checkingOpening && now < nextCheck) mounted = false;
                else if (panel != 0)
                {
                    checkingOpening = false;
                    if (mounted) nextCheck = long.MaxValue;
                    else if (now >= nextCheck || nextCheck == long.MaxValue)
                    {
                        nextCheck = now + RecoveryCheckInterval;
                        nint bridge = bridges.Count == 0 ? 0 : bridges[0];
                        var state = bridge == 0 ? QuickSettingsBridgeState.Missing : ReadBridgeState(bridge);
                        switch (recovery.Check(now, bridge, state))
                        {
                            case QuickSettingsRecoveryAction.Attach:
                                TryAttach(shellPid, ref runtime);
                                nextCheck = recovery.DelayAfterAttach(Environment.TickCount64);
                                break;
                            case QuickSettingsRecoveryAction.Recycle:
                                Trace.WriteLine($"Quick Settings recycling bridge in ShellHost {shellPid}, status {state}.");
                                PostMessage(bridge, RecycleMessage, _controller, 0);
                                break;
                        }
                    }
                }

                if (!fallback && recovery.ShowTray && !mounted) FindBridges(shellPid, out mounted);
                if (mounted && !checkingOpening) recovery.ConfirmMount();
                if (revision != Revision || !_enabled.WaitOne(0)) continue;
                if (fallback != recovery.ShowTray) _trayFallback(revision, fallback = recovery.ShowTray);
                if (signal is 1 or 2 && (mounted || HasMount(bridges))) _click(signal == 2);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Quick Settings bridge: " + ex.Message);
            if (_enabled.WaitOne(0) && !_stop.IsCancellationRequested) _trayFallback(Revision, true);
        }
    }

    private static bool IsVisiblePanel(nint window, int shellPid) =>
        window != 0 && shellPid != 0 && IsWindowVisible(window) &&
        GetWindowThreadProcessId(window, out uint pid) != 0 && pid == shellPid;

    private static nint FindVisiblePanel(int shellPid)
    {
        nint window = 0;
        while ((window = FindWindowEx(0, window, PanelClass, null)) != 0)
            if (IsVisiblePanel(window, shellPid)) return window;
        return 0;
    }

    private List<nint> FindBridges(int shellPid, out bool mounted)
    {
        var bridges = new List<nint>();
        nint window = 0;
        while ((window = FindWindowEx(new nint(-3), window, null, null)) != 0)
        {
            if (GetProp(window, ContextProperty) != _controller) continue;
            if (GetWindowThreadProcessId(window, out uint pid) != 0 && pid == shellPid) bridges.Add(window);
        }
        mounted = HasMount(bridges);
        return bridges;
    }

    private bool HasMount(List<nint> bridges) => bridges.Any(window => GetProp(window, MountedProperty) == _controller);

    private QuickSettingsBridgeState ReadBridgeState(nint bridge)
    {
        const uint abortIfHungOrExited = 0x0002 | 0x0020;
        if (SendMessageTimeout(bridge, QueryStateMessage, _controller, 0, abortIfHungOrExited, 100, out nuint result) == 0)
            return QuickSettingsBridgeState.Missing;
        return result is >= 1 and <= 3 ? (QuickSettingsBridgeState)result : QuickSettingsBridgeState.Missing;
    }

    private void TryAttach(int pid, ref string? runtime)
    {
        try
        {
            runtime ??= StageComponent();
            Trace.WriteLine($"Quick Settings attachment to ShellHost {pid}: 0x{Attach(pid, runtime):X8}.");
        }
        catch (Exception ex) { Trace.WriteLine($"Quick Settings connection to ShellHost {pid}: {ex.Message}"); }
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

    private int Attach(int pid, string path)
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
                return result;
            }
            return notFound;
        }
        finally { NativeLibrary.Free(library); }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        if (_showHook != 0) UnhookWinEvent(_showHook);
        RemoveProp(_controller, ControllerProperty);
        _enabled.Reset();
        _owner.Reset();
        _stop.Cancel();
        _ = _worker.ContinueWith(_ =>
        {
            _owner.Dispose();
            _enabled.Dispose();
            _mountChanged.Dispose();
            _panelShown.Dispose();
            _left.Dispose();
            _right.Dispose();
            _stop.Dispose();
        }, TaskScheduler.Default);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int InitializeDiagnostics(string endpoint, uint pid, string diagnostics, string tap, Guid clsid, string? data);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(nint window, string property, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string property);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string property);
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
