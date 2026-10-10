using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

public readonly record struct FlipToStartSnapshot(
    bool Supported,
    bool Enabled,
    string Backend);

public readonly record struct FlipToStartResult(
    bool Success,
    string Message,
    FlipToStartSnapshot Snapshot);

public static class LenovoFlipToStartHelper
{
    // LLT CapabilityID.FlipToStart = 0x00030000.
    private const uint FlipToStartCapabilityId = 0x00030000;

    // LLT UEFI fallback.
    private const string FirmwareGuid = "{D743491E-F484-4952-A87D-8D5DD189B70C}";
    private const string FirmwareVariable = "FBSWIF";
    private const uint FirmwareAttributes = 0x00000007;

    public static FlipToStartSnapshot Read()
    {
        if (TryReadCapability(out bool enabled))
            return new(true, enabled, "Capability");

        if (TryReadUefi(out enabled))
            return new(true, enabled, "UEFI");

        return new(false, false, string.Empty);
    }

    public static Task<FlipToStartResult> SetAsync(bool enabled) => Task.Run(() =>
    {
        try
        {
            FlipToStartSnapshot before = Read();
            if (!before.Supported)
                return new FlipToStartResult(false, "此设备未暴露开盖开机控制接口。", before);

            if (before.Backend == "Capability")
                SetCapability(enabled);
            else
                SetUefi(enabled);

            FlipToStartSnapshot after = Read();
            bool success = after.Supported && after.Enabled == enabled;
            return new(
                success,
                success ? string.Empty : $"设置已发送，但当前开盖开机仍为：{(after.Enabled ? "开启" : "关闭")}",
                after);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, new(false, enabled, string.Empty));
        }
    });

    private static bool TryReadCapability(out bool enabled)
    {
        enabled = false;
        try
        {
            // Match LLT AbstractCapabilityFeature.IsSupportedAsync(): the
            // capability backend is valid only when LENOVO_CAPABILITY_DATA_00
            // explicitly advertises FlipToStart. Otherwise LLT falls through
            // to the UEFI backend.
            if (!HasFlipToStartCapability())
                return false;

            using ManagementObject? obj = GetLenovoOtherMethod();
            if (obj is null)
                return false;

            using ManagementBaseObject inParams = obj.GetMethodParameters("GetFeatureValue");
            inParams["IDs"] = (int)FlipToStartCapabilityId;
            using ManagementBaseObject? outParams = obj.InvokeMethod("GetFeatureValue", inParams, null);
            if (outParams is null)
                return false;

            int value = Convert.ToInt32(outParams["Value"]);
            if (value is not (0 or 1))
                return false;

            enabled = value == 1;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasFlipToStartCapability()
    {
        using var searcher = new ManagementObjectSearcher(
            "root\\WMI",
            "SELECT IDs FROM LENOVO_CAPABILITY_DATA_00");
        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (Convert.ToUInt32(item["IDs"]) == FlipToStartCapabilityId)
                    return true;
            }
        }

        return false;
    }

    private static void SetCapability(bool enabled)
    {
        using ManagementObject? obj = GetLenovoOtherMethod();
        if (obj is null)
            throw new InvalidOperationException("未找到 LENOVO_OTHER_METHOD。 ");

        using ManagementBaseObject inParams = obj.GetMethodParameters("SetFeatureValue");
        inParams["IDs"] = (int)FlipToStartCapabilityId;
        inParams["value"] = enabled ? 1 : 0;
        using ManagementBaseObject? outParams = obj.InvokeMethod("SetFeatureValue", inParams, null);
    }

    private static ManagementObject? GetLenovoOtherMethod()
    {
        using var searcher = new ManagementObjectSearcher(
            "root\\WMI",
            "SELECT * FROM LENOVO_OTHER_METHOD");
        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementObject item in results)
        {
            using (item)
                return new ManagementObject(item.Path);
        }

        return null;
    }

    private static bool TryReadUefi(out bool enabled)
    {
        enabled = false;
        try
        {
            using var privilege = SystemEnvironmentPrivilege.TryEnable();
            if (!privilege.Enabled)
                return false;

            byte[] buffer = new byte[4];
            uint read = GetFirmwareEnvironmentVariableExW(
                FirmwareVariable,
                FirmwareGuid,
                buffer,
                (uint)buffer.Length,
                out _);

            if (read == 0)
                return false;

            enabled = buffer[0] != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetUefi(bool enabled)
    {
        using var privilege = SystemEnvironmentPrivilege.TryEnable();
        if (!privilege.Enabled)
            throw new InvalidOperationException("无法启用 SeSystemEnvironmentPrivilege。 ");

        byte[] buffer = [enabled ? (byte)1 : (byte)0, 0, 0, 0];
        if (!SetFirmwareEnvironmentVariableExW(
                FirmwareVariable,
                FirmwareGuid,
                buffer,
                (uint)buffer.Length,
                FirmwareAttributes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "写入 FBSWIF UEFI 变量失败");
        }
    }

    private sealed class SystemEnvironmentPrivilege : IDisposable
    {
        private IntPtr _token;
        public bool Enabled { get; private set; }

        private SystemEnvironmentPrivilege()
        {
        }

        public static SystemEnvironmentPrivilege TryEnable()
        {
            var scope = new SystemEnvironmentPrivilege();
            scope.Enable();
            return scope;
        }

        private void Enable()
        {
            if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TokenAdjustPrivileges | TokenQuery,
                    out _token))
                return;

            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out LUID luid))
                return;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };

            if (!AdjustTokenPrivileges(
                    _token,
                    false,
                    ref tp,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
                return;

            Enabled = Marshal.GetLastWin32Error() == 0;
        }

        public void Dispose()
        {
            if (_token == IntPtr.Zero)
                return;

            if (Enabled && LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out LUID luid))
            {
                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES
                    {
                        Luid = luid,
                        Attributes = 0
                    }
                };
                _ = AdjustTokenPrivileges(
                    _token,
                    false,
                    ref tp,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }

            _ = CloseHandle(_token);
            _token = IntPtr.Zero;
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(
        string? lpSystemName,
        string lpName,
        out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string lpName,
        string lpGuid,
        [Out] byte[] pBuffer,
        uint nSize,
        out uint pdwAttribubutes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFirmwareEnvironmentVariableExW(
        string lpName,
        string lpGuid,
        byte[] pValue,
        uint nSize,
        uint dwAttributes);
}
