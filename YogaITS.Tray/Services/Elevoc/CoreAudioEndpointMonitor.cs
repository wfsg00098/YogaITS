using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services.Elevoc;

internal enum AudioDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

internal enum AudioRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

internal sealed record AudioEndpointInfo(
    AudioDataFlow Flow,
    string Id,
    string FriendlyName,
    Guid? JackSubType = null)
{
    // Built-in array classification is conservative: unknown/ordinary/headset
    // microphones do not expose the directional sub-options.
    public bool IsInternalMicrophone => Flow == AudioDataFlow.Capture && JackSubType is Guid type &&
        (type == new Guid("DFF21BE5-F70F-11D0-B917-00A0C9223196") ||
         type == new Guid("DFF21BE6-F70F-11D0-B917-00A0C9223196"));
    public bool LooksLikeInternalDmic
    {
        get
        {
            string n = FriendlyName ?? string.Empty;
            return n.Contains("麦克风阵列", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Microphone Array", StringComparison.OrdinalIgnoreCase)
                || n.Contains("数字麦克风", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Digital Microphone", StringComparison.OrdinalIgnoreCase);
        }
    }
}

internal sealed class CoreAudioEndpointMonitor : IDisposable
{
    private const uint STGM_READ = 0;

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly EndpointNotificationClient _notification;
    private bool _disposed;

    public event EventHandler? DefaultEndpointChanged;

    public CoreAudioEndpointMonitor()
    {
        Type enumeratorType =
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;

        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        _notification = new EndpointNotificationClient(this);

        int hr = _enumerator.RegisterEndpointNotificationCallback(_notification);
        Marshal.ThrowExceptionForHR(hr);
    }

    public AudioEndpointInfo? GetDefault(AudioDataFlow flow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int hr = _enumerator.GetDefaultAudioEndpoint(flow, AudioRole.Console, out IMMDevice? device);
        if (hr < 0 || device is null)
            return null;

        try
        {
            string id = GetDeviceId(device);
            string name = GetStringProperty(device, PropertyKeys.DeviceFriendlyName) ?? id;
            string? jack = GetStringProperty(device, PropertyKeys.JackSubType);
            return new AudioEndpointInfo(flow, id, name, Guid.TryParse(jack, out var type) ? type : null);
        }
        finally
        {
            if (Marshal.IsComObject(device))
                Marshal.FinalReleaseComObject(device);
        }
    }

    private static string GetDeviceId(IMMDevice device)
    {
        int hr = device.GetId(out nint ptr);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally
        {
            if (ptr != 0)
                Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static string? GetStringProperty(IMMDevice device, PropertyKey key)
    {
        int hr = device.OpenPropertyStore(STGM_READ, out IPropertyStore? store);
        if (hr < 0 || store is null)
            return null;

        try
        {
            hr = store.GetValue(ref key, out PropVariant value);
            if (hr < 0)
                return null;

            try
            {
                return value.VarType switch
                {
                    31 => Marshal.PtrToStringUni(value.PointerValue), // VT_LPWSTR
                    _ => null
                };
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            if (Marshal.IsComObject(store))
                Marshal.FinalReleaseComObject(store);
        }
    }

    private void RaiseDefaultEndpointChanged()
    {
        if (!_disposed)
            DefaultEndpointChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _ = _enumerator.UnregisterEndpointNotificationCallback(_notification);
        }
        catch
        {
        }

        if (Marshal.IsComObject(_enumerator))
            Marshal.FinalReleaseComObject(_enumerator);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly CoreAudioEndpointMonitor _owner;

        public EndpointNotificationClient(CoreAudioEndpointMonitor owner)
        {
            _owner = owner;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            _owner.RaiseDefaultEndpointChanged();
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            _owner.RaiseDefaultEndpointChanged();
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            _owner.RaiseDefaultEndpointChanged();
            return 0;
        }

        public int OnDefaultDeviceChanged(
            AudioDataFlow flow,
            AudioRole role,
            string? defaultDeviceId)
        {
            if (flow is AudioDataFlow.Capture or AudioDataFlow.Render)
                _owner.RaiseDefaultEndpointChanged();

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            AudioDataFlow dataFlow,
            uint stateMask,
            out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            AudioDataFlow dataFlow,
            AudioRole role,
            out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            uint clsCtx,
            nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        [PreserveSig]
        int OpenPropertyStore(
            uint accessMode,
            out IPropertyStore? properties);

        [PreserveSig]
        int GetId(out nint id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            uint newState);

        [PreserveSig]
        int OnDeviceAdded(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(
            AudioDataFlow flow,
            AudioRole role,
            [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    private static class PropertyKeys
    {
        public static PropertyKey JackSubType =>
            new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 8);

        public static PropertyKey DeviceFriendlyName =>
            new(
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                14);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VarType;

        [FieldOffset(8)]
        public nint PointerValue;
    }

    [DllImport("Ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);
}
