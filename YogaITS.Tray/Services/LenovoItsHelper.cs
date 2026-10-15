using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace YogaITS.Tray.Services;

public sealed record ItsModeSnapshot(ItsMode? Mode, int? RawState, string DisplayName);

public sealed record ItsSetResult(ItsModeSnapshot Snapshot, bool Verified);

public readonly record struct ItsCapabilitySnapshot(
    bool Supported,
    bool GeekModeSupported,
    bool ShowGeekAsCreatorMode,
    int DispatcherVersion,
    int ItsFnCapability)
{
    public bool UsesLegacyService => ItsModePolicy.UsesLegacyService(DispatcherVersion);

    public string FourthModeDisplayName => ShowGeekAsCreatorMode
        ? "创作模式"
        : "极客模式";
}

public static class LenovoItsHelper
{
    private const string ServiceName = "LenovoProcessManagement";
    private const string LegacyServiceName = "LITSSVC";
    private const string LegacyRegistryPath = @"SYSTEM\CurrentControlSet\Services\LITSSVC\LNBITS\IC\MMC";
    private const string RegistryPath =
        @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";

    // Matches current LLT ITSModeFeature.
    private const uint IoctlEnergySmartPower = 0x8310213C;
    private static readonly SemaphoreSlim ModeSetGate = new(1, 1);
    private static volatile bool _legacyGeekModeActive;

    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public static ItsCapabilitySnapshot ReadCapabilities(string model)
    {
        if (!SupportsItsModeByModel(model))
            return default;

        int version = ReadDispatcherInt("Version");
        int fnCapability = ReadDispatcherInt("ITS_FN_Capability");
        try
        {
            if (ItsModePolicy.UsesLegacyService(version))
                return ReadLegacyCapabilities(version, fnCapability);
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            if (key is null) return new(false, false, false, version, fnCapability);

            bool showGeekAsCreator = (fnCapability & 0x20) != 0;

            bool currentModeRead = QueryEnergySmartPower(ItsCapabilityPolicy.CurrentModeQuery, out uint currentMode);
            if (!ItsCapabilityPolicy.SupportsIts(currentModeRead, currentMode))
                return new(false, false, showGeekAsCreator, version, fnCapability);

            bool geekSupported;
            try
            {
                bool capabilityRead = QueryEnergySmartPower(ItsCapabilityPolicy.ExtendedCapabilitiesQuery, out uint capability);
                // LLT requires a readable DWORD, not an active fourth mode.
                // Do not fall back to ITS_CurrentSetting for this capability check.
                geekSupported = ItsCapabilityPolicy.SupportsFourthMode(
                    capabilityRead, capability, key.GetValue("ITS_CurrentSettingV"));
            }
            catch
            {
                // Failure to query the fourth-mode
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
            return new(false, false, false, version, fnCapability);
        }
    }

    public static ItsModeSnapshot ReadMode(ItsCapabilitySnapshot capabilities)
    {
        if (!capabilities.Supported)
            return new(null, null, "不可用");

        try
        {
            if (capabilities.UsesLegacyService)
                return ReadLegacyMode(capabilities);
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

            ItsMode? mode = ItsModePolicy.FromCurrentSetting(state);

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
            throw new InvalidOperationException("此机型未检测到可用的 ITS 模式切换能力。");

        if (mode == ItsMode.Creator && !capabilities.GeekModeSupported)
            throw new InvalidOperationException("此机型未检测到 Geek/创作模式能力。");

        int[] commands = ItsModePolicy.ServiceCommands(capabilities.UsesLegacyService, mode);
        string serviceName = capabilities.UsesLegacyService ? LegacyServiceName : ServiceName;
        await ModeSetGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (mode == ItsMode.Creator &&
                    !ItsCapabilityPolicy.CanSelectFourthMode(SystemEventMonitor.ReadPowerSource()))
                    throw new InvalidOperationException("极客/创作模式需要连接电源适配器。");
                using var service = new ServiceController(serviceName);
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Running)
                    throw new InvalidOperationException($"{serviceName} 服务没有运行。");

                if (capabilities.UsesLegacyService && mode != ItsMode.Creator)
                {
                    // LLT clears the old Geek override best-effort before basic modes.
                    try { SetLegacyGeekMode(false); } catch { }
                }
                foreach (int command in commands)
                    service.ExecuteCommand(command);
                if (capabilities.UsesLegacyService && mode == ItsMode.Creator)
                    SetLegacyGeekMode(true);
            }).ConfigureAwait(false);

            // LLT allows the service up to 3 seconds, checking every 200 ms.
            var elapsed = Stopwatch.StartNew();
            ItsModeSnapshot snapshot;
            do
            {
                snapshot = await Task.Run(() => ReadMode(capabilities)).ConfigureAwait(false);
                if (snapshot.Mode == mode) return new(snapshot, true);
                await Task.Delay(200).ConfigureAwait(false);
            }
            while (elapsed.Elapsed < TimeSpan.FromSeconds(3));
            return new(snapshot, false);
        }
        finally { ModeSetGate.Release(); }
    }

    private static ItsCapabilitySnapshot ReadLegacyCapabilities(int version, int fnCapability)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(LegacyRegistryPath, writable: false);
        if (key is null || !IsEnergyDriverPresent())
            return new(false, false, false, version, fnCapability);

        bool geekSupported = false;
        try
        {
            bool success = QueryEnergySmartPower(ItsCapabilityPolicy.ExtendedCapabilitiesQuery, out uint capability);
            geekSupported = ItsCapabilityPolicy.SupportsLegacyFourthMode(success, capability);
        }
        catch { /* Basic legacy modes do not depend on the fourth-mode query. */ }
        return new(true, geekSupported, (fnCapability & 0x20) != 0, version, fnCapability);
    }

    private static ItsModeSnapshot ReadLegacyMode(ItsCapabilitySnapshot capabilities)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(LegacyRegistryPath, writable: false);
        if (key is null) return new(null, null, "无法读取");
        int automatic = ReadRegistryInt(key, "AutomaticModeSetting", -1);
        int current = ReadRegistryInt(key, "CurrentSetting", -1);
        ItsMode? mode = ItsModePolicy.FromLegacySettings(automatic, current, _legacyGeekModeActive);
        return mode is ItsMode actual
            ? new(actual, current, GetDisplayName(actual, capabilities))
            : new(null, current < 0 ? null : current, $"未知模式 (Auto={automatic}, Current={current})");
    }

    private static void SetLegacyGeekMode(bool enabled)
    {
        uint command = enabled ? ItsModePolicy.LegacyGeekEnabled : ItsModePolicy.LegacyGeekDisabled;
        if (!QueryEnergySmartPower(command, out _))
            throw new InvalidOperationException("旧版极客模式设置未成功。");
        _legacyGeekModeActive = enabled;
    }

    private static bool IsEnergyDriverPresent()
    {
        // Match LLT's presence probe: access denied still proves the device exists.
        using SafeFileHandle handle = CreateFileW(@"\\.\EnergyDrv", 0,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        return !handle.IsInvalid || Marshal.GetLastWin32Error() == 5;
    }

    private static int ReadDispatcherInt(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            return key is null ? 0 : ReadRegistryInt(key, name, 0);
        }
        catch { return 0; } // LLT treats missing/unreadable dispatcher metadata as zero.
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

    private static bool QueryEnergySmartPower(uint input, out uint output)
    {
        using SafeFileHandle handle = OpenEnergyDriver();

        bool ok = DeviceIoControl(
            handle,
            IoctlEnergySmartPower,
            ref input,
            sizeof(uint),
            out output,
            sizeof(uint),
            out uint bytesReturned,
            IntPtr.Zero);

        return ok && bytesReturned == sizeof(uint);
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
