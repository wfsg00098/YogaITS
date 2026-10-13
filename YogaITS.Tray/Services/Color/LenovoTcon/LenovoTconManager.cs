using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services.Color.LenovoTcon;

internal sealed class NativeTconWmi : IDisposable
{
    private const string NamespaceName = @"root\wmi";
    private const string ClassName = "LENOVO_TCON_DATA";
    private const string GateMethod = "SetTconSwitch";

    private WbemNative.IWbemLocator? _locator;
    private WbemNative.IWbemServices? _services;
    private WbemNative.IWbemClassObject? _classObject;
    private WbemNative.IWbemClassObject? _instanceObject;
    private bool _coInitOwned;

    public int HrCoInitializeEx { get; private set; } = int.MinValue;
    public int HrCoInitializeSecurity { get; private set; } = int.MinValue;
    public int HrConnect { get; private set; } = int.MinValue;
    public int HrProxyServices { get; private set; } = int.MinValue;
    public int HrGetClass { get; private set; } = int.MinValue;
    public int HrGetMethod { get; private set; } = int.MinValue;

    // Exact manager attempt: no blanket is applied to IEnumWbemClassObject.
    public int HrCreateEnumExact { get; private set; } = int.MinValue;
    public int HrNextExact { get; private set; } = int.MinValue;
    public uint ReturnedExact { get; private set; }

    // Diagnostic fallback only: recreate enum + apply the same manager blanket to it.
    public int HrCreateEnumRetry { get; private set; } = int.MinValue;
    public int HrProxyEnumRetry { get; private set; } = int.MinValue;
    public int HrNextRetry { get; private set; } = int.MinValue;
    public uint ReturnedRetry { get; private set; }

    public int HrGetPath { get; private set; } = int.MinValue;
    public string InstancePath { get; private set; } = "<none>";
    public bool UsedEnumBlanketFallback { get; private set; }
    public bool Ready { get; private set; }
    public string ReadyMode => Ready
        ? (UsedEnumBlanketFallback ? "READY (enum-blanket fallback)" : "READY (Lenovo-exact)")
        : HrGetClass == WbemNative.WBEM_E_NOT_FOUND
            ? "UNAVAILABLE (LENOVO_TCON_DATA not found)"
            : "UNAVAILABLE";

    public NativeTconWmi()
    {
        try { Initialize(); }
        catch { Dispose(); throw; }
    }

    private void Initialize()
    {
        // Lenovo DLL does CoInitializeEx(NULL, MTA), then CoInitializeSecurity with
        // DEFAULT authn / IMPERSONATE and explicitly accepts RPC_E_TOO_LATE.
        HrCoInitializeEx = WbemNative.CoInitializeEx(IntPtr.Zero, WbemNative.COINIT_MULTITHREADED);
        _coInitOwned = HrCoInitializeEx == WbemNative.S_OK || HrCoInitializeEx == WbemNative.S_FALSE;
        if (HrCoInitializeEx < 0 && HrCoInitializeEx != WbemNative.RPC_E_CHANGED_MODE)
            return;

        HrCoInitializeSecurity = WbemNative.CoInitializeSecurity(
            IntPtr.Zero,
            -1,
            IntPtr.Zero,
            IntPtr.Zero,
            WbemNative.RPC_C_AUTHN_LEVEL_DEFAULT,
            WbemNative.RPC_C_IMP_LEVEL_IMPERSONATE,
            IntPtr.Zero,
            WbemNative.EOAC_NONE,
            IntPtr.Zero);
        if (HrCoInitializeSecurity < 0 && HrCoInitializeSecurity != WbemNative.RPC_E_TOO_LATE)
            return;

        Type locatorType = Type.GetTypeFromCLSID(WbemNative.CLSID_WbemLocator, throwOnError: true)!;
        _locator = (WbemNative.IWbemLocator)Activator.CreateInstance(locatorType)!;

        HrConnect = _locator.ConnectServer(NamespaceName, null, null, null, 0, null, IntPtr.Zero, out var services);
        if (!WbemNative.Succeeded(HrConnect)) return;
        _services = services;

        // Exact recovered Lenovo arguments:
        // WINNT / AUTHZ_NONE / principal=NULL / CALL / IMPERSONATE / auth=NULL / EOAC_NONE.
        HrProxyServices = WbemNative.ApplyManagerProxyBlanket(_services);
        if (!WbemNative.Succeeded(HrProxyServices)) return;

        HrGetClass = _services.GetObject(ClassName, 0, IntPtr.Zero, out var classObj, out _);
        if (!WbemNative.Succeeded(HrGetClass)) return;
        _classObject = classObj;

        HrGetMethod = _classObject.GetMethod(GateMethod, 0, out var inSig, out var outSig);
        WbemNative.ReleaseCom(inSig);
        WbemNative.ReleaseCom(outSig);
        if (!WbemNative.Succeeded(HrGetMethod)) return;

        // Attempt 1: mirror Lenovo exactly. The vendor DLL calls CreateInstanceEnum(0x30)
        // and then Next without CoSetProxyBlanket on the enum. Production bounds
        // enumeration to 5 seconds rather than the probe's infinite wait.
        if (TryEnumerateExact(out var instance))
        {
            _instanceObject = instance;
        }
        else if (HrNextExact == WbemNative.WBEM_E_ACCESS_DENIED)
        {
            // Attempt 2 is diagnostic, not vendor-exact. Managed COM interop can produce a
            // separate enum proxy; try applying the same blanket to that proxy explicitly.
            if (TryEnumerateWithEnumBlanket(out instance))
            {
                _instanceObject = instance;
                UsedEnumBlanketFallback = true;
            }
        }

        if (_instanceObject is null) return;

        HrGetPath = _instanceObject.Get("__PATH", 0, out object pathValue, out _, out _);
        if (!WbemNative.Succeeded(HrGetPath)) return;
        InstancePath = Convert.ToString(pathValue) ?? "<null>";
        if (string.IsNullOrWhiteSpace(InstancePath)) return;

        Ready = true;
    }

    private bool TryEnumerateExact(out WbemNative.IWbemClassObject? instance)
    {
        instance = null;
        if (_services is null) return false;

        HrCreateEnumExact = _services.CreateInstanceEnum(
            ClassName,
            WbemNative.WBEM_FLAG_RETURN_IMMEDIATELY | WbemNative.WBEM_FLAG_FORWARD_ONLY,
            IntPtr.Zero,
            out var enumerator);
        if (!WbemNative.Succeeded(HrCreateEnumExact)) return false;

        try
        {
            var one = new WbemNative.IWbemClassObject[1];
            HrNextExact = enumerator.Next(5000, 1, one, out uint returned);
            ReturnedExact = returned;
            if (WbemNative.Succeeded(HrNextExact) && returned >= 1 && one[0] is not null)
            {
                instance = one[0];
                return true;
            }
            if (one[0] is not null) WbemNative.ReleaseCom(one[0]);
            return false;
        }
        finally
        {
            WbemNative.ReleaseCom(enumerator);
        }
    }

    private bool TryEnumerateWithEnumBlanket(out WbemNative.IWbemClassObject? instance)
    {
        instance = null;
        if (_services is null) return false;

        HrCreateEnumRetry = _services.CreateInstanceEnum(
            ClassName,
            WbemNative.WBEM_FLAG_RETURN_IMMEDIATELY | WbemNative.WBEM_FLAG_FORWARD_ONLY,
            IntPtr.Zero,
            out var enumerator);
        if (!WbemNative.Succeeded(HrCreateEnumRetry)) return false;

        try
        {
            HrProxyEnumRetry = WbemNative.ApplyManagerProxyBlanket(enumerator);
            if (!WbemNative.Succeeded(HrProxyEnumRetry)) return false;

            var one = new WbemNative.IWbemClassObject[1];
            HrNextRetry = enumerator.Next(5000, 1, one, out uint returned);
            ReturnedRetry = returned;
            if (WbemNative.Succeeded(HrNextRetry) && returned >= 1 && one[0] is not null)
            {
                instance = one[0];
                return true;
            }
            if (one[0] is not null) WbemNative.ReleaseCom(one[0]);
            return false;
        }
        finally
        {
            WbemNative.ReleaseCom(enumerator);
        }
    }

    public void LogPreflight(Action<string> log)
    {
        log($"Worker CoInitializeEx(MTA)        : {Fmt(HrCoInitializeEx)}");
        log($"Worker CoInitializeSecurity      : {Fmt(HrCoInitializeSecurity)}");
        log($"ConnectServer(root\\wmi)          : {Fmt(HrConnect)}");
        log($"CoSetProxyBlanket(IWbemServices) : {Fmt(HrProxyServices)}");
        log($"GetObject({ClassName})    : {Fmt(HrGetClass)}");
        log($"GetMethod({GateMethod})        : {Fmt(HrGetMethod)}");
        log("[Lenovo-exact enumeration]");
        log($"  CreateInstanceEnum(flags=0x30): {Fmt(HrCreateEnumExact)}");
        log($"  Next(5000 ms,1)               : {Fmt(HrNextExact)}  returned={ReturnedExact}");

        if (HrNextExact == WbemNative.WBEM_E_ACCESS_DENIED)
        {
            log("  classification                 : ACCESS_DENIED (not an empty class)");
            log("[diagnostic enum-proxy blanket retry]");
            log($"  CreateInstanceEnum             : {Fmt(HrCreateEnumRetry)}");
            log($"  CoSetProxyBlanket(enum)        : {Fmt(HrProxyEnumRetry)}");
            log($"  Next(5000 ms,1)               : {Fmt(HrNextRetry)}  returned={ReturnedRetry}");
        }

        log($"instance __PATH Get              : {Fmt(HrGetPath)}");
        log($"instance __PATH                  : {InstancePath}");
        log($"MANAGER WMI BACKEND              : {ReadyMode}");
    }

    private static string Fmt(int hr)
        => hr == int.MinValue ? "<not reached>" : $"{WbemNative.Hr(hr)}  {WbemNative.ExplainHr(hr)}";

    public int SetGate(bool enabled, out string detail)
    {
        detail = "";
        if (!Ready || _services is null || _classObject is null)
        {
            detail = "Native manager preflight is not READY.";
            return unchecked((int)0x80004005);
        }

        WbemNative.IWbemClassObject? inSig = null;
        WbemNative.IWbemClassObject? outSig = null;
        WbemNative.IWbemClassObject? inParams = null;
        WbemNative.IWbemClassObject? outParams = null;
        try
        {
            int hr = _classObject.GetMethod(GateMethod, 0, out inSig, out outSig);
            if (!WbemNative.Succeeded(hr)) { detail = "GetMethod failed"; return hr; }
            if (inSig is null) { detail = "GetMethod returned NULL input signature"; return unchecked((int)0x80004005); }

            hr = inSig.SpawnInstance(0, out inParams);
            if (!WbemNative.Succeeded(hr)) { detail = "SpawnInstance failed"; return hr; }

            object data = enabled ? "1" : "0";
            hr = inParams.Put("Data", 0, ref data, 0);
            if (!WbemNative.Succeeded(hr)) { detail = "Put(Data) failed"; return hr; }

            hr = _services.ExecMethod(InstancePath, GateMethod, 0, IntPtr.Zero, inParams, out outParams, IntPtr.Zero);
            detail = $"Data=\"{data}\"; preflight={ReadyMode}";
            return hr;
        }
        finally
        {
            WbemNative.ReleaseCom(outParams);
            WbemNative.ReleaseCom(inParams);
            WbemNative.ReleaseCom(outSig);
            WbemNative.ReleaseCom(inSig);
        }
    }

    public void Dispose()
    {
        WbemNative.ReleaseCom(_instanceObject);
        WbemNative.ReleaseCom(_classObject);
        WbemNative.ReleaseCom(_services);
        WbemNative.ReleaseCom(_locator);
        _instanceObject = null;
        _classObject = null;
        _services = null;
        _locator = null;
        if (_coInitOwned) WbemNative.CoUninitialize();
    }
}

internal static class TconTransport
{
    internal const string DevicePath = @"\\.\SPBTESTTOOL";
    internal const string ParallelMutexName = @"Global\TconDriverConnectorParallelMutex";

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint IOCTL_SESSION_INIT = 0x04001C00;
    private const uint IOCTL_WRITE_READ = 0x04001C10;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_ABANDONED = 0x00000080;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(
        IntPtr lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle hMutex);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, int nInBufferSize,
        [Out] byte[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlNoBuffers(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    internal static void RandomDelay() => Thread.Sleep(Random.Shared.Next(10, 30));
    internal static void PostIoDelay() => Thread.Sleep(10);

    internal sealed class Session : IDisposable
    {
        private readonly SafeWaitHandle? _mutex;
        private bool _mutexOwned;
        internal SafeFileHandle Handle { get; }
        internal bool IsReady => _mutexOwned && !Handle.IsInvalid;
        internal uint WaitResult { get; }
        internal int OpenWin32Error { get; }

        internal Session(SafeWaitHandle? mutex, bool mutexOwned, uint waitResult, SafeFileHandle handle, int openWin32Error)
        {
            _mutex = mutex;
            _mutexOwned = mutexOwned;
            WaitResult = waitResult;
            Handle = handle;
            OpenWin32Error = openWin32Error;
        }

        public void Dispose()
        {
            try
            {
                if (!Handle.IsClosed) Handle.Dispose();
            }
            finally
            {
                if (_mutexOwned && _mutex is not null && !_mutex.IsInvalid && !_mutex.IsClosed)
                {
                    ReleaseMutex(_mutex);
                    _mutexOwned = false;
                }
                _mutex?.Dispose();
            }
        }
    }

    // Directly recovered from SEColorManagerPlugin64.dll 1.0.140.11181:
    // outer random 10..29 ms -> CreateMutexW("Global\\TconDriverConnectorParallelMutex")
    // -> WaitForSingleObject(INFINITE) -> CreateFileW("\\\\.\\SPBTESTTOOL")
    // -> session-init transaction (another random 10..29 ms, gate ON, IOCTL 0x04001C00,
    //    gate OFF, Sleep(10)) -> actual gate ON + F3/F0/WriteFile -> gate OFF
    // -> CloseHandle -> ReleaseMutex/CloseHandle(mutex) -> Sleep(10).
    internal static Session OpenSerialized(Action<string> log)
    {
        RandomDelay();

        SafeWaitHandle mutex = CreateMutexW(IntPtr.Zero, false, ParallelMutexName);
        if (mutex.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            log($"parallel mutex      : CREATE FAIL win32={e} {new Win32Exception(e).Message}");
            return new Session(mutex, false, WAIT_FAILED, new SafeFileHandle(IntPtr.Zero, ownsHandle: true), e);
        }

        uint wr = WaitForSingleObject(mutex, 5000);
        bool owned = wr == WAIT_OBJECT_0 || wr == WAIT_ABANDONED;
        log($"parallel mutex      : {ParallelMutexName}");
        log($"WaitForSingleObject : 0x{wr:X8} {(wr == WAIT_OBJECT_0 ? "WAIT_OBJECT_0" : wr == WAIT_ABANDONED ? "WAIT_ABANDONED" : wr == WAIT_FAILED ? "WAIT_FAILED" : "OTHER")}");
        if (!owned)
        {
            int e = wr == 0x102 ? 1460 : Marshal.GetLastWin32Error();
            return new Session(mutex, false, wr, new SafeFileHandle(IntPtr.Zero, ownsHandle: true), e);
        }

        SafeFileHandle h = CreateFileW(DevicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        int openErr = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        log($"SPB handle          : {(h.IsInvalid ? "OPEN FAIL" : "OPEN PASS")} (manager serialized session)");
        if (h.IsInvalid)
            log($"CreateFileW detail  : win32={openErr} {new Win32Exception(openErr).Message}");

        return new Session(mutex, true, wr, h, openErr);
    }

    internal static IoctlResult SessionInitOpened(NativeTconWmi wmi, SafeFileHandle h, Action<string> log)
    {
        if (h.IsInvalid)
            return new(false, Marshal.GetLastWin32Error(), 0, Array.Empty<byte>(), "invalid SPB handle");

        // This is a distinct transaction performed by PC Manager immediately after opening SPB.
        RandomDelay();
        bool gateOn = false;
        try
        {
            int hrOn = wmi.SetGate(true, out string onDetail);
            log($"[session-init] SetTconSwitch(\"1\") : {WbemNative.Hr(hrOn)}  {onDetail}");
            if (!WbemNative.Succeeded(hrOn))
                return new(false, hrOn, 0, Array.Empty<byte>(), "session-init WMI gate ON failed");
            gateOn = true;

            bool ok = DeviceIoControlNoBuffers(h, IOCTL_SESSION_INIT,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out int returned, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            log($"[session-init] DeviceIoControl 0x{IOCTL_SESSION_INIT:X8}: {(ok ? "PASS" : "FAIL")} bytesReturned={returned} win32={err}");
            if (!ok) log($"[session-init] detail: {new Win32Exception(err).Message}");
            return new(ok, err, returned, Array.Empty<byte>(), ok ? "OK" : new Win32Exception(err).Message);
        }
        finally
        {
            if (gateOn)
            {
                int hrOff = wmi.SetGate(false, out string offDetail);
                log($"[session-init] SetTconSwitch(\"0\") : {WbemNative.Hr(hrOff)}  {offDetail}");
            }
            PostIoDelay();
        }
    }

    internal static IoctlResult ReadOpened(SafeFileHandle h, byte[] input, int outputSize)
    {
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            return new(false, e, 0, Array.Empty<byte>(), "invalid SPB handle: " + new Win32Exception(e).Message);
        }

        var output = new byte[outputSize];
        bool ok = DeviceIoControl(h, IOCTL_WRITE_READ, input, input.Length, output, output.Length, out int returned, IntPtr.Zero);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        return new(ok, err, returned, output, ok ? "OK" : new Win32Exception(err).Message);
    }

    internal static WriteResult WriteOpened(SafeFileHandle h, byte[] data)
    {
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            return new(false, e, 0, "invalid SPB handle: " + new Win32Exception(e).Message);
        }

        bool ok = WriteFile(h, data, data.Length, out int written, IntPtr.Zero);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        return new(ok, err, written, ok ? "OK" : new Win32Exception(err).Message);
    }
}

internal readonly record struct IoctlResult(bool Success, int Win32Error, int BytesReturned, byte[] Output, string Detail);
internal readonly record struct WriteResult(bool Success, int Win32Error, int BytesWritten, string Detail);

internal readonly record struct AbilityResult(bool Success, ushort RawAbility, int AdjustedAbility, IoctlResult Ioctl);
internal readonly record struct GamutResult(bool Success, byte RawMode, string Name, IoctlResult Ioctl);

internal static class LenovoManagerPath
{
    internal const int GamutAbilityBit = 0x08;
    // Mode-specific capability bits recovered from SEColorManagerPlugin64.dll 1.0.140.11181.
    // Capability serialization slots line up with the color-type enum order:
    // slot 1 -> enum 2 Adobe, slot 2 -> enum 3 sRGB, slot 3 -> enum 4 DisplayP3.
    internal const int P3AbilityBit = 0x100;
    internal const int SrgbAbilityBit = 0x200;
    internal const int AdobeRgbAbilityBit = 0x400;

    private static IoctlResult ManagerReadIo(NativeTconWmi wmi, byte[] input, int outputSize, string command, Action<string> log)
    {
        var session = TconTransport.OpenSerialized(log);
        try
        {
            if (!session.IsReady)
            {
                var fail = new IoctlResult(false, session.OpenWin32Error, 0, Array.Empty<byte>(), "serialized SPB session could not be opened");
                LogIo(log, command, fail);
                return fail;
            }

            // PC Manager performs a separate 0x04001C00 initialization transaction on the same
            // SPB handle before every real F3/F0/WriteFile operation.
            IoctlResult init = TconTransport.SessionInitOpened(wmi, session.Handle, log);
            if (!init.Success)
                log("[session-init] warning: matching the verified vendor path, continue to the live capability/GET transaction.");

            bool gateOn = false;
            try
            {
                int hrOn = wmi.SetGate(true, out string onDetail);
                log($"SetTconSwitch(\"1\") : {WbemNative.Hr(hrOn)}  {onDetail}");
                if (!WbemNative.Succeeded(hrOn))
                    return new(false, hrOn, 0, Array.Empty<byte>(), "WMI gate ON failed");
                gateOn = true;

                var io = TconTransport.ReadOpened(session.Handle, input, outputSize);
                LogIo(log, command, io);
                return io;
            }
            finally
            {
                if (gateOn)
                {
                    int hrOff = wmi.SetGate(false, out string offDetail);
                    log($"SetTconSwitch(\"0\") : {WbemNative.Hr(hrOff)}  {offDetail}");
                }
            }
        }
        finally
        {
            // Vendor order after the real transaction: CloseHandle(SPB) -> ReleaseMutex ->
            // CloseHandle(mutex) -> Sleep(10).
            session.Dispose();
            TconTransport.PostIoDelay();
        }
    }

    private static WriteResult ManagerWriteIo(NativeTconWmi wmi, byte[] data, Action<string> log)
    {
        var session = TconTransport.OpenSerialized(log);
        try
        {
            if (!session.IsReady)
                return new(false, session.OpenWin32Error, 0, "serialized SPB session could not be opened");

            IoctlResult init = TconTransport.SessionInitOpened(wmi, session.Handle, log);
            if (!init.Success)
                log("[session-init] warning: matching the verified vendor path, write result must still pass live readback.");

            bool gateOn = false;
            try
            {
                int hrOn = wmi.SetGate(true, out string onDetail);
                log($"SetTconSwitch(\"1\") : {WbemNative.Hr(hrOn)}  {onDetail}");
                if (!WbemNative.Succeeded(hrOn))
                    return new(false, hrOn, 0, "WMI gate ON failed");
                gateOn = true;

                return TconTransport.WriteOpened(session.Handle, data);
            }
            finally
            {
                if (gateOn)
                {
                    int hrOff = wmi.SetGate(false, out string offDetail);
                    log($"SetTconSwitch(\"0\") : {WbemNative.Hr(hrOff)}  {offDetail}");
                }
            }
        }
        finally
        {
            session.Dispose();
            TconTransport.PostIoDelay();
        }
    }

    internal static AbilityResult ReadAbility(NativeTconWmi wmi, Action<string> log)
    {
            var io = ManagerReadIo(wmi, new byte[] { 0xF3, 0x00 }, 4, "F3 00", log);
            if (!io.Success)
                return new(false, 0, 0, io);
            if (!ColorBackendPolicy.CanReadTconBuffer(io.Success, io.Output.Length, 4))
                return new(false, 0, 0, io);
            if (io.BytesReturned != 4)
                log($"TCON capability buffer is 4 bytes; driver reports {io.BytesReturned}. Using the verified probe buffer semantics.");

            ushort raw = (ushort)(io.Output[0] | (io.Output[3] << 8));
            int adjusted = raw;
            if ((raw & 0x16) == 0x16) adjusted += 0x20;
            log($"ability raw      : 0x{raw:X4} ({raw})");
            log($"ability adjusted : 0x{adjusted:X4} ({adjusted})");
            log($"gamut bit 0x08   : {(((adjusted & GamutAbilityBit) != 0) ? "YES" : "NO")}");
            log($"P3 bit 0x0100    : {(((adjusted & P3AbilityBit) != 0) ? "YES" : "NO")}");
            log($"sRGB bit 0x0200  : {(((adjusted & SrgbAbilityBit) != 0) ? "YES" : "NO")}");
            log($"Adobe bit 0x0400 : {(((adjusted & AdobeRgbAbilityBit) != 0) ? "YES (code-mapped; hardware unverified)" : "NO")}");
            return new(true, raw, adjusted, io);
    }

    internal static GamutResult ReadGamut(NativeTconWmi wmi, Action<string> log)
    {
            var io = ManagerReadIo(wmi, new byte[] { 0xF0, 0x1D }, 1, "F0 1D", log);
            if (!io.Success)
                return new(false, 0xFF, "<unavailable>", io);

            if (!ColorBackendPolicy.CanReadTconBuffer(io.Success, io.Output.Length, 1))
                return new(false, 0xFF, "<unavailable>", io);
            if (io.BytesReturned != 1)
                log($"TCON mode buffer is 1 byte; driver reports {io.BytesReturned}. Using the verified probe buffer semantics.");

            byte raw = io.Output[0];
            string name = GamutName(raw);
            log($"current gamut raw: 0x{raw:X2}  {name}");
            return new(true, raw, name, io);
    }

    internal static bool SetGamut(byte target, Action<string> log)
    {
        int targetAbilityBit = ModeAbilityBit(target);
        if (targetAbilityBit == 0)
        {
            log($"SET REFUSED: unsupported target 0x{target:X2}.");
            return false;
        }

        using var wmi = new NativeTconWmi();
        wmi.LogPreflight(log);
        if (!wmi.Ready)
        {
            log("SET REFUSED: native PC Manager WMI preflight is not READY.");
            return false;
        }

        log("");
        log("[safety] live ability check");
        AbilityResult ability = ReadAbility(wmi, log);
        if (!ability.Success || (ability.AdjustedAbility & GamutAbilityBit) == 0)
        {
            log("SET REFUSED: live ability query did not prove gamut bit 0x08.");
            return false;
        }
        if ((ability.AdjustedAbility & targetAbilityBit) == 0)
        {
            log($"SET REFUSED: live ability does not advertise {GamutName(target)} (required bit 0x{targetAbilityBit:X4}).");
            return false;
        }
        if (target == 0x08)
            log("WARNING: Adobe RGB mapping is recovered from Lenovo code but has not been verified on a positive real-hardware sample.");

        log("");
        log("[safety] current gamut GET");
        GamutResult before = ReadGamut(wmi, log);
        if (!before.Success)
        {
            log("SET REFUSED: current gamut GET failed.");
            return false;
        }

        log("");
        log($"[write] target 0x{target:X2} {GamutName(target)}");
        WriteResult wr = ManagerWriteIo(wmi, new byte[] { 0xF0, 0x1D, target }, log);
        log($"WriteFile F0 1D {target:X2}: {(wr.Success ? "PASS" : "FAIL")} bytesWritten={wr.BytesWritten} win32={wr.Win32Error} detail={wr.Detail}");
        if (!wr.Success) return false;

        log("");
        log("[verify] manager-style readback, max 6 attempts / 30 ms");
        for (int i = 1; i <= 6; i++)
        {
            if (i > 1) Thread.Sleep(30);
            log($"verify attempt {i}/6");
            GamutResult after = ReadGamut(wmi, log);
            if (after.Success && after.RawMode == target)
            {
                log($"VERIFY PASS: 0x{after.RawMode:X2} {after.Name}");
                return true;
            }
        }

        log("VERIFY FAIL: target was not observed within 6 reads.");
        return false;
    }

    internal static int ModeAbilityBit(byte raw) => ColorBackendPolicy.ModeBit(raw);

    internal static bool SupportsMode(int adjustedAbility, byte raw)
    {
        return ColorBackendPolicy.SupportsTconMode(adjustedAbility, raw);
    }

    internal static string AvailableModeSummary(int adjustedAbility)
    {
        if ((adjustedAbility & GamutAbilityBit) == 0) return "none (gamut bit 0x08 = 0)";
        var modes = new List<string>();
        if ((adjustedAbility & SrgbAbilityBit) != 0) modes.Add("sRGB");
        if ((adjustedAbility & P3AbilityBit) != 0) modes.Add("P3");
        if ((adjustedAbility & AdobeRgbAbilityBit) != 0) modes.Add("Adobe RGB（未实机验证）");
        return modes.Count == 0 ? "none advertised" : string.Join(" / ", modes).Trim();
    }

    internal static string GamutName(byte raw) => raw switch
    {
        0x04 => "P3",
        0x06 => "sRGB",
        0x08 => "Adobe RGB (hardware unverified)",
        _ => "OTHER/UNKNOWN"
    };

    private static void LogIo(Action<string> log, string command, IoctlResult io)
    {
        string raw = io.Output is { Length: > 0 } ? string.Join(" ", io.Output.Select(b => b.ToString("X2"))) : "<none>";
        log($"DeviceIoControl {command}: {(io.Success ? "PASS" : "FAIL")} bytesReturned={io.BytesReturned} win32={io.Win32Error}");
        log($"output buffer      : {raw}");
        if (!string.Equals(io.Detail, "OK", StringComparison.Ordinal)) log($"detail             : {io.Detail}");
    }
}
