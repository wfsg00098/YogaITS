using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

internal sealed class SystemEventMonitor : IDisposable
{
    private const nuint SubclassId = 0x59475055;
    private static readonly Guid AcDcSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private readonly nint _hwnd;
    private readonly SubclassProc _callback;
    private nint _powerNotification;
    private CancellationTokenSource? _displayDebounce;
    public event Action? PowerChanged;
    public event Action? DisplayChanged;

    public SystemEventMonitor(nint hwnd)
    {
        _hwnd = hwnd;
        _callback = OnMessage;
        if (!SetWindowSubclass(hwnd, _callback, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void EnablePowerNotifications()
    {
        if (_powerNotification != 0) return;
        Guid guid = AcDcSource;
        _powerNotification = RegisterPowerSettingNotification(_hwnd, ref guid, 0);
        if (_powerNotification == 0)
            System.Diagnostics.Debug.WriteLine("GPU power notification registration failed.");
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0218 && wParam == 0x8013 && lParam != 0 &&
            Marshal.PtrToStructure<Guid>(lParam) == AcDcSource)
            PowerChanged?.Invoke();
        else if (message == 0x007E)
        {
            _displayDebounce?.Cancel();
            _displayDebounce?.Dispose();
            _displayDebounce = new CancellationTokenSource();
            _ = NotifyDisplayAsync(_displayDebounce.Token);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private async Task NotifyDisplayAsync(CancellationToken token)
    {
        try { await Task.Delay(750, token); DisplayChanged?.Invoke(); }
        catch (OperationCanceledException) { }
    }

    public static int ReadPowerSource() => GetSystemPowerStatus(out var status)
        ? status.AcLineStatus switch { 0 => 1, 1 => 0, _ => -1 } : -1;

    public void Dispose()
    {
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
