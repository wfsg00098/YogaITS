using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

[Flags]
internal enum SystemStateChange { None = 0, Power = 1, Display = 2, Appearance = 4 }

internal sealed class SystemEventMonitor : IDisposable
{
    private const nuint SubclassId = 0x59475055;
    private static readonly Guid AcDcSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private readonly nint _hwnd;
    private readonly SubclassProc _callback;
    private nint _powerNotification;
    private CancellationTokenSource? _displayDebounce;
    private bool _disposed;
    public event Action? PowerChanged;
    public event Action? DisplayChanged;
    public event Action? AppearanceChanged;

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

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (_disposed) return DefSubclassProc(hwnd, message, wParam, lParam);
        try
        {
            Guid? setting = message == 0x0218 && wParam == 0x8013 && lParam != 0
                ? Marshal.PtrToStructure<Guid>(lParam) : null;
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
