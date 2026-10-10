using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

public enum BatteryChargeMode
{
    Conservation,
    Normal,
    RapidCharge
}

public readonly record struct BatteryChargeSnapshot(
    bool Supported,
    BatteryChargeMode Mode,
    string DisplayName);

public readonly record struct BatteryToggleSnapshot(
    bool Supported,
    bool Enabled);

public readonly record struct BatteryActionResult(
    bool Success,
    string Message,
    BatteryChargeSnapshot Snapshot);

public readonly record struct BatteryToggleResult(
    bool Success,
    string Message,
    BatteryToggleSnapshot Snapshot);

public static class LenovoBatteryHelper
{
    // Same EnergyDrv protocol used by Lenovo Legion Toolkit.
    private const uint IoctlBatteryChargeMode = 0x831020F8;
    private const uint IoctlBatteryNightCharge = 0x83102150;

    private const uint ChargeModeRead = 0xFF;
    private const uint NightChargeRead = 0x11;

    private const string BatteryRegistryPath = @"Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin";
    private const string BatteryRegistryName = "BatteryChargeMode";

    // Match LLT Drivers.GetEnergy(): FILE_READ_DATA | FILE_WRITE_DATA,
    // not GENERIC_READ | GENERIC_WRITE.
    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public static BatteryChargeSnapshot ReadChargeMode()
    {
        try
        {
            using SafeFileHandle handle = OpenEnergyDriver();
            uint raw = SendCode(handle, IoctlBatteryChargeMode, ChargeModeRead);
            BatteryChargeMode mode = DecodeChargeMode(raw);
            return new(true, mode, GetDisplayName(mode));
        }
        catch
        {
            return new(false, BatteryChargeMode.Normal, "不可用");
        }
    }

    public static BatteryToggleSnapshot ReadNightCharge()
    {
        try
        {
            using SafeFileHandle handle = OpenEnergyDriver();
            uint raw = SendCode(handle, IoctlBatteryNightCharge, NightChargeRead);
            return new(true, DecodeNightCharge(raw));
        }
        catch
        {
            return new(false, false);
        }
    }

    public static Task<BatteryActionResult> SetChargeModeAsync(BatteryChargeMode mode) =>
        Task.Run(async () =>
        {
            try
            {
                using SafeFileHandle handle = OpenEnergyDriver();

                // LLT BatteryFeature.ToInternalAsync():
                // Conservation => 0x08, 0x03
                // Normal       => 0x05, 0x08
                // RapidCharge  => 0x05, 0x07
                uint[] codes = mode switch
                {
                    BatteryChargeMode.Conservation => [0x08, 0x03],
                    BatteryChargeMode.Normal => [0x05, 0x08],
                    BatteryChargeMode.RapidCharge => [0x05, 0x07],
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };

                foreach (uint code in codes)
                    _ = SendCode(handle, IoctlBatteryChargeMode, code);

                BatteryChargeMode actual = await VerifyChargeModeAsync(handle, mode).ConfigureAwait(false);
                SetCompatibilityRegistry(actual);

                bool success = actual == mode;
                var snapshot = new BatteryChargeSnapshot(true, actual, GetDisplayName(actual));
                return new BatteryActionResult(
                    success,
                    success ? string.Empty : $"命令已发送，但当前充电模式仍为：{snapshot.DisplayName}",
                    snapshot);
            }
            catch (Exception ex)
            {
                return new BatteryActionResult(
                    false,
                    ex.Message,
                    new BatteryChargeSnapshot(false, mode, GetDisplayName(mode)));
            }
        });

    public static Task<BatteryToggleResult> SetNightChargeAsync(bool enabled) =>
        Task.Run(async () =>
        {
            try
            {
                using SafeFileHandle handle = OpenEnergyDriver();
                uint command = enabled ? 0x80000012u : 0x12u;
                _ = SendCode(handle, IoctlBatteryNightCharge, command);

                bool actual = await VerifyNightChargeAsync(handle, enabled).ConfigureAwait(false);
                bool success = actual == enabled;
                return new BatteryToggleResult(
                    success,
                    success ? string.Empty : $"命令已发送，但夜间充电优化当前仍为：{(actual ? "开启" : "关闭")}",
                    new BatteryToggleSnapshot(true, actual));
            }
            catch (Exception ex)
            {
                return new BatteryToggleResult(
                    false,
                    ex.Message,
                    new BatteryToggleSnapshot(false, enabled));
            }
        });

    private static async Task<BatteryChargeMode> VerifyChargeModeAsync(
        SafeFileHandle handle,
        BatteryChargeMode target)
    {
        BatteryChargeMode actual = target;
        for (int i = 0; i < 10; i++)
        {
            actual = DecodeChargeMode(SendCode(handle, IoctlBatteryChargeMode, ChargeModeRead));
            if (actual == target)
                return actual;

            await Task.Delay(50).ConfigureAwait(false);
        }

        return actual;
    }

    private static async Task<bool> VerifyNightChargeAsync(
        SafeFileHandle handle,
        bool target)
    {
        bool actual = target;
        for (int i = 0; i < 10; i++)
        {
            actual = DecodeNightCharge(SendCode(handle, IoctlBatteryNightCharge, NightChargeRead));
            if (actual == target)
                return actual;

            await Task.Delay(50).ConfigureAwait(false);
        }

        return actual;
    }

    private static BatteryChargeMode DecodeChargeMode(uint raw)
    {
        // LLT: Storage/conservation bit 0x20 has priority over Express bit 0x04.
        if ((raw & 0x20) != 0)
            return BatteryChargeMode.Conservation;
        if ((raw & 0x04) != 0)
            return BatteryChargeMode.RapidCharge;
        return BatteryChargeMode.Normal;
    }

    private static bool DecodeNightCharge(uint raw)
    {
        // LLT requires bit 0 to indicate a valid response, then bit 4 carries state.
        if ((raw & 0x01) == 0)
            throw new InvalidOperationException($"无法识别夜间充电状态：0x{raw:X8}");

        return (raw & 0x10) != 0;
    }

    private static string GetDisplayName(BatteryChargeMode mode) => mode switch
    {
        BatteryChargeMode.Conservation => "养护模式",
        BatteryChargeMode.Normal => "常规模式",
        BatteryChargeMode.RapidCharge => "快充模式",
        _ => "未知"
    };

    private static void SetCompatibilityRegistry(BatteryChargeMode mode)
    {
        string value = mode switch
        {
            BatteryChargeMode.Normal => "Normal",
            BatteryChargeMode.RapidCharge => "Quick",
            BatteryChargeMode.Conservation => "Storage",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(BatteryRegistryPath, true);
            key?.SetValue(BatteryRegistryName, value, RegistryValueKind.String);
        }
        catch
        {
            // Hardware state is authoritative. Registry sync is only for Lenovo/Vantage compatibility.
        }
    }

    private static SafeFileHandle OpenEnergyDriver()
    {
        SafeFileHandle handle = CreateFileW(
            @"\\.\EnergyDrv",
            FileReadData | FileWriteData,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "无法打开 Lenovo EnergyDrv");
        }

        return handle;
    }

    private static uint SendCode(SafeFileHandle handle, uint controlCode, uint input)
    {
        uint output = 0;
        bool ok = DeviceIoControl(
            handle,
            controlCode,
            ref input,
            sizeof(uint),
            out output,
            sizeof(uint),
            out _,
            IntPtr.Zero);

        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"EnergyDrv DeviceIoControl 失败：0x{controlCode:X8}");

        return output;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref uint lpInBuffer,
        int nInBufferSize,
        out uint lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
