namespace YogaITS.Tray.Services.Color;

using System.Runtime.InteropServices;

internal static class NativeMethods
{
    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE = 16;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_SET_WCG_STATE = 17;
    internal const int DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR = 0;
    internal const int DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG = 1;
    internal const int DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR = 2;

    internal const int WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER = 1;
    internal const int CPT_ICC = 0;
    internal const int CPST_RGB_WORKING_SPACE = 5;
    internal const int CPST_STANDARD_DISPLAY_COLOR_MODE = 7;

    internal const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    internal const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint OPEN_EXISTING = 3;
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public override readonly string ToString() => $"0x{unchecked((ulong)(((long)HighPart << 32) | LowPart)):X16}";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
        public int activeColorMode;

        // User-enabled bits are preferences, not the effective display mode.
        public readonly bool HdrActive => activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;
        public readonly bool AcmActive => activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG;
        public readonly bool HighDynamicRangeUserEnabled => (value & (1u << 5)) != 0;
        public readonly bool WideColorUserEnabled => (value & (1u << 7)) != 0;
        public readonly bool HighDynamicRangeSupported => (value & (1u << 4)) != 0;
        public readonly bool WideColorSupported => (value & (1u << 6)) != 0;
        public readonly bool LimitedByPolicy => (value & (1u << 3)) != 0;
    }

    // SET_HDR_STATE and SET_WCG_STATE have identical header + one-bit payload layouts.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DISPLAYCONFIG_SET_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    internal static extern int DisplayConfigSetColorState(ref DISPLAYCONFIG_SET_COLOR_STATE request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount,
        [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    internal static extern int DisplayConfigGetTargetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    internal static extern int DisplayConfigGetSourceDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetAdvancedColorInfo2(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, IntPtr lpDevMode,
        IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileGetDisplayUserScope(LUID adapterId, uint sourceId, out int scope);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileGetDisplayDefault(int scope, LUID adapterId, uint sourceId,
        int profileType, int profileSubtype, out IntPtr profileName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profileName,
        int profileType, int profileSubtype, LUID adapterId, uint sourceId);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileAddDisplayAssociation(int scope, string profileName,
        LUID adapterId, uint sourceId,
        [MarshalAs(UnmanagedType.Bool)] bool setAsDefault,
        [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ColorProfileRemoveDisplayAssociation(int scope, string profileName,
        LUID adapterId, uint sourceId,
        [MarshalAs(UnmanagedType.Bool)] bool dissociateAdvancedColor);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InstallColorProfileW(string? pMachineName, string pProfileName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UninstallColorProfileW(string? pMachineName, string pProfileName,
        [MarshalAs(UnmanagedType.Bool)] bool bDelete);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WcsSetDefaultColorProfile(int scope, string? deviceName,
        int profileType, int profileSubtype, uint profileId, string? profileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize, byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
}
