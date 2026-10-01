using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace YogaITS.Tray.Services;

public enum ItsMode
{
    Intelligent,
    Cool,
    Performance,
    Creator
}

public sealed record ItsModeSnapshot(ItsMode? Mode, int? RawState, string DisplayName);

public sealed record ItsSetResult(ItsModeSnapshot Snapshot, bool Verified);

public readonly record struct ItsCapabilitySnapshot(
    bool Supported,
    bool GeekModeSupported,
    bool ShowGeekAsCreatorMode,
    int DispatcherVersion,
    int ItsFnCapability)
{
    public string FourthModeDisplayName => ShowGeekAsCreatorMode
        ? "创作模式"
        : "极客模式";
}

public static class LenovoItsHelper
{
    private const string ServiceName = "LenovoProcessManagement";
    private const string RegistryPath =
        @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";

    // Matches current LLT ITSModeFeature.
    private const int DispatcherVersion3 = 8192; // 0x2000
    private const uint IoctlEnergySmartPower = 0x8310213C;
    private const uint GeekModeCapabilityQuery = 10;
    private const uint GeekModeCapabilityMask = 0x00020001;

    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private static readonly IReadOnlyDictionary<ItsMode, (int ControlCode, int State)> Modes =
        new Dictionary<ItsMode, (int, int)>
        {
            [ItsMode.Intelligent] = (163, 0),
            [ItsMode.Cool] = (164, 1),
            [ItsMode.Performance] = (165, 3),
            [ItsMode.Creator] = (172, 4)
        };

    public static ItsCapabilitySnapshot ReadCapabilities(string model)
    {
        if (!SupportsItsModeByModel(model))
            return default;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            if (key is null)
                return default;

            int version = ReadRegistryInt(key, "Version", 0);

            // This app intentionally implements LLT's modern LenovoProcessManagement
            // path only. Do not expose controls on a legacy LITSSVC machine that we
            // cannot safely drive with the modern service-control commands.
            if (version < DispatcherVersion3)
                return new(false, false, false, version, 0);

            int fnCapability = ReadRegistryInt(key, "ITS_FN_Capability", 0);
            bool showGeekAsCreator = (fnCapability & 0x20) != 0;

            bool geekSupported;
            try
            {
                geekSupported = QueryGeekModeCapability();
            }
            catch
            {
                // Same behavior as LLT: failure to query the fourth-mode
                // capability removes only Geek/Creator, not the three basic ITS modes.
                geekSupported = false;
            }

            return new(
                Supported: true,
                GeekModeSupported: geekSupported,
                ShowGeekAsCreatorMode: showGeekAsCreator,
                DispatcherVersion: version,
                ItsFnCapability: fnCapability);
        }
        catch
        {
            return default;
        }
    }

    public static ItsModeSnapshot ReadMode(ItsCapabilitySnapshot capabilities)
    {
        if (!capabilities.Supported)
            return new(null, null, "不可用");

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            if (key is null)
                return new(null, null, "无法读取");

            // Same selection logic as current LLT: bit 0x10 decides which
            // ITS current-setting value is authoritative, with fallback.
            bool useVersioned = (capabilities.ItsFnCapability & 0x10) != 0;
            string primaryName = useVersioned ? "ITS_CurrentSettingV" : "ITS_CurrentSetting";
            string fallbackName = useVersioned ? "ITS_CurrentSetting" : "ITS_CurrentSettingV";

            int state = ReadRegistryInt(key, primaryName, -1);
            if (state == -1)
                state = ReadRegistryInt(key, fallbackName, -1);

            if (state == -1)
                return new(null, null, "无法读取");

            ItsMode? mode = state switch
            {
                0 => ItsMode.Intelligent,
                1 => ItsMode.Cool,
                3 => ItsMode.Performance,
                4 => ItsMode.Creator,
                _ => null
            };

            if (mode is null)
                return new(null, state, $"未知模式 ({state})");

            return new(mode, state, GetDisplayName(mode.Value, capabilities));
        }
        catch
        {
            return new(null, null, "无法读取");
        }
    }

    public static ItsModeSnapshot ReadMode()
    {
        MachineInfoSnapshot machine = LenovoMachineInfoHelper.Read();
        ItsCapabilitySnapshot capabilities = ReadCapabilities(machine.Model);
        return ReadMode(capabilities);
    }

    public static async Task<ItsSetResult> SetModeAsync(
        ItsMode mode,
        ItsCapabilitySnapshot capabilities)
    {
        if (!capabilities.Supported)
            throw new InvalidOperationException("此机型未检测到可用的现代 ITS 模式切换能力。");

        if (mode == ItsMode.Creator && !capabilities.GeekModeSupported)
            throw new InvalidOperationException("此机型未检测到 Geek/创作模式能力。");

        var spec = Modes[mode];

        await Task.Run(() =>
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();

            if (service.Status != ServiceControllerStatus.Running)
                throw new InvalidOperationException($"{ServiceName} 服务没有运行。");

            service.ExecuteCommand(spec.ControlCode);
        });

        // One delayed verification read only. No polling loop.
        await Task.Delay(350);
        ItsModeSnapshot snapshot = ReadMode(capabilities);

        return new(
            Snapshot: snapshot,
            Verified: snapshot.RawState == spec.State
        );
    }

    public static Task<ItsSetResult> SetModeAsync(ItsMode mode)
    {
        MachineInfoSnapshot machine = LenovoMachineInfoHelper.Read();
        return SetModeAsync(mode, ReadCapabilities(machine.Model));
    }

    public static string GetDisplayName(
        ItsMode mode,
        ItsCapabilitySnapshot capabilities) => mode switch
    {
        ItsMode.Intelligent => "智能模式",
        ItsMode.Cool => "安静模式",
        ItsMode.Performance => "性能模式",
        ItsMode.Creator => capabilities.FourthModeDisplayName,
        _ => "未知"
    };

    private static bool SupportsItsModeByModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        // Mirrors LLT Compatibility.GetSupportITSMode().
        if (model.Contains("IdeaPad Gaming", StringComparison.OrdinalIgnoreCase))
            return false;

        return model.Contains("IdeaPad", StringComparison.OrdinalIgnoreCase)
            || model.Contains("ThinkBook", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Lenovo Slim", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Motobook", StringComparison.OrdinalIgnoreCase)
            || model.Contains("XiaoXin", StringComparison.OrdinalIgnoreCase)
            || model.Contains("YOGA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool QueryGeekModeCapability()
    {
        using SafeFileHandle handle = OpenEnergyDriver();
        uint input = GeekModeCapabilityQuery;
        uint output = 0;

        bool ok = DeviceIoControl(
            handle,
            IoctlEnergySmartPower,
            ref input,
            sizeof(uint),
            out output,
            sizeof(uint),
            out _,
            IntPtr.Zero);

        return ok && (output & GeekModeCapabilityMask) == GeekModeCapabilityMask;
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

    private static int ReadRegistryInt(RegistryKey key, string valueName, int defaultValue)
    {
        return key.GetValue(valueName, defaultValue) switch
        {
            int i => i,
            long l => (int)l,
            uint u => (int)u,
            short s => s,
            ushort us => us,
            byte b => b,
            _ => defaultValue
        };
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
