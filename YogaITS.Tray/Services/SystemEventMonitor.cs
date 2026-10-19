using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

[Flags]
internal enum SystemStateChange { None = 0, Power = 1, Display = 2, Appearance = 4 }

internal sealed class SystemEventMonitor : IDisposable
{
    private const nuint SubclassId = 0x59475055;
    private static readonly Guid AcDcSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private readonly nint _hwnd;
    private readonly SubclassProc _callback;
    private nint _powerNotification;
    private nint _displayPowerNotification;
    private CancellationTokenSource? _displayDebounce;
    private bool _disposed;
    public event Action? PowerChanged;
    public event Action? DisplayChanged;
    public event Action? AppearanceChanged;
    public event Action<bool>? SleepStateChanged;
    public event Action<bool>? DisplayPowerChanged;

    public SystemEventMonitor(nint hwnd)
    {
        _hwnd = hwnd;
        _callback = OnMessage;
        if (!SetWindowSubclass(hwnd, _callback, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public bool EnablePowerNotifications()
    {
        if (_disposed) return false;
        if (_powerNotification != 0) return true;
        Guid guid = AcDcSource;
        _powerNotification = RegisterPowerSettingNotification(_hwnd, ref guid, 0);
        if (_powerNotification == 0)
            System.Diagnostics.Debug.WriteLine("GPU power notification registration failed.");
        return _powerNotification != 0;
    }

    public bool EnableDisplayPowerNotifications()
    {
        if (_disposed) return false;
        if (_displayPowerNotification != 0) return true;
        Guid guid = SessionDisplayStatus;
        _displayPowerNotification = RegisterPowerSettingNotification(_hwnd, ref guid, 0);
        if (_displayPowerNotification == 0)
            System.Diagnostics.Debug.WriteLine("Display power notification registration failed.");
        return _displayPowerNotification != 0;
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (_disposed) return DefSubclassProc(hwnd, message, wParam, lParam);
        try
        {
            if (ReadSleepState(message, wParam) is { } suspended) SleepStateChanged?.Invoke(suspended);
            Guid? setting = message == 0x0218 && wParam == 0x8013 && lParam != 0
                ? Marshal.PtrToStructure<Guid>(lParam) : null;
            // POWERBROADCAST_SETTING: GUID (16 bytes), DWORD length, then data.
            // This is independent of topology changes and must not be debounced.
            if (setting == SessionDisplayStatus && Marshal.ReadInt32(lParam, 16) == sizeof(uint) &&
                ReadDisplayState(setting, sizeof(uint), unchecked((uint)Marshal.ReadInt32(lParam, 20))) is { } displayOn)
                DisplayPowerChanged?.Invoke(displayOn);
            var change = ClassifyMessage(message, wParam, setting);
            if ((change & SystemStateChange.Power) != 0) PowerChanged?.Invoke();
            if ((change & SystemStateChange.Appearance) != 0) AppearanceChanged?.Invoke();
            if ((change & SystemStateChange.Display) != 0)
            {
                _displayDebounce?.Cancel();
                _displayDebounce?.Dispose();
                _displayDebounce = new CancellationTokenSource();
                _ = NotifyDisplayAsync(_displayDebounce.Token);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("System event dispatch failed: " + ex.Message); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    internal static SystemStateChange ClassifyMessage(uint message, nint wParam, Guid? setting = null)
    {
        if (message is 0x001A or 0x031A) // WM_SETTINGCHANGE / WM_THEMECHANGED
            return SystemStateChange.Appearance;
        if (message == 0x0218) // WM_POWERBROADCAST
        {
            if (wParam == 0x8013 && setting == AcDcSource || wParam == 0x000A)
                return SystemStateChange.Power; // AC/DC setting or general power status.
            if (wParam == 0x0012 || wParam == 0x0007)
                return SystemStateChange.Power | SystemStateChange.Display; // Resume.
        }
        // A monitor/dock can arrive without changing the desktop resolution.
        if (message == 0x007E || message == 0x0219 && wParam == 0x0007)
            return SystemStateChange.Display; // WM_DISPLAYCHANGE / DBT_DEVNODES_CHANGED.
        return SystemStateChange.None;
    }

    internal static bool? ReadSleepState(uint message, nint wParam) => message != 0x0218 ? null :
        wParam == 4 ? true : wParam == 7 || wParam == 0x12 ? false : null;

    internal static bool? ReadDisplayState(Guid? setting, uint length, uint value) =>
        setting != SessionDisplayStatus || length != sizeof(uint) ? null :
        value switch { 0 => false, 1 or 2 => true, _ => null }; // Dimmed is still on.

    private async Task NotifyDisplayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(750, token);
            if (!_disposed && !token.IsCancellationRequested) DisplayChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
    }

    public static int ReadPowerSource() => GetSystemPowerStatus(out var status)
        ? status.AcLineStatus switch { 0 => 1, 1 => 0, _ => -1 } : -1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _displayDebounce?.Cancel();
        _displayDebounce?.Dispose();
        if (_powerNotification != 0) UnregisterPowerSettingNotification(_powerNotification);
        if (_displayPowerNotification != 0) UnregisterPowerSettingNotification(_displayPowerNotification);
        RemoveWindowSubclass(_hwnd, _callback, SubclassId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, Reserved;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(nint hwnd, ref Guid setting, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
