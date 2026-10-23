using System.Runtime.InteropServices;

namespace YogaITS.Tray.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct NvGpuDisplayId
{
    public const uint Version = 16 | (3u << 16); // NVIDIA NV_GPU_DISPLAYIDS_VER2 uses revision 3.
    public uint StructureVersion;
    public int ConnectorType;
    public uint DisplayId;
    public uint Flags;
    public readonly bool IsConnected => (Flags & (1u << 6)) != 0;
}

internal interface INvidiaDisplayApi : IDisposable
{
    int Initialize();
    int Enumerate(nint[] handles, out int count);
    int GetDisplays(nint gpu, NvGpuDisplayId[]? displays, ref uint count);
}

internal static class NvidiaDisplayQuery
{
    public static DgpuDisplayQueryResult Query()
    {
        // Share initialization/unload serialization with the process-list query.
        lock (NvidiaGpuHelper.NvapiGate) return Query(() => new NativeApi());
    }

    internal static DgpuDisplayQueryResult Query(Func<INvidiaDisplayApi> create)
    {
        string stage = "Load nvapi64.dll / nvapi_QueryInterface";
        int? status = null;
        try
        {
            using var api = create();
            stage = "NvAPI_Initialize";
            status = api.Initialize();
            if (status != 0) return Unknown();
            var handles = new nint[64];
            stage = "NvAPI_EnumPhysicalGPUs";
            status = api.Enumerate(handles, out int gpuCount);
            if (status != 0) return Unknown();
            if (gpuCount is < 1 or > 64) return Unknown("Invalid physical GPU count: " + gpuCount);
            for (int gpu = 0; gpu < gpuCount; gpu++)
            {
                if (handles[gpu] == 0) return Unknown("Null physical GPU handle");
                uint count = 0;
                stage = $"NvAPI_GPU_GetConnectedDisplayIds GPU={gpu} count";
                status = api.GetDisplays(handles[gpu], null, ref count);
                if (status != 0) return Unknown();
                if (count == 0) continue;
                // Bound untrusted allocation; topology races fail closed rather than retry SET.
                if (count > 4096) return Unknown("Invalid display count: " + count);
                var displays = new NvGpuDisplayId[count];
                for (int i = 0; i < displays.Length; i++) displays[i].StructureVersion = NvGpuDisplayId.Version;
                stage = $"NvAPI_GPU_GetConnectedDisplayIds GPU={gpu} data";
                status = api.GetDisplays(handles[gpu], displays, ref count);
                if (status != 0) return Unknown();
                if (count > displays.Length) return Unknown("Display count grew beyond the allocated buffer");
                for (int i = 0; i < count; i++)
                    if (displays[i].IsConnected)
                        return new(DgpuDisplayState.Connected, NativeCode: 0, Detail: $"GPU={gpu}, displayId={displays[i].DisplayId}");
            }
            return new(DgpuDisplayState.Disconnected, NativeCode: 0, Detail: $"Enumerated {gpuCount} physical GPU(s); no connected display.");
        }
        catch (Exception ex)
        {
            // No NVAPI status was returned for a managed exception; don't reuse
            // a previous successful call's zero as this failure's native code.
            return new(DgpuDisplayState.Unknown, Detail: stage + ": " + ex);
        }

        DgpuDisplayQueryResult Unknown(string? detail = null) => new(DgpuDisplayState.Unknown,
            NativeCode: status, Detail: stage + (detail is null ? "" : ": " + detail));
    }

    private sealed class NativeApi : INvidiaDisplayApi
    {
        private nint _library;
        private readonly NoArg _initialize;
        private readonly NoArg _unload;
        private readonly EnumGpus _enumerate;
        private readonly GetDisplayIds _getDisplays;
        private bool _initialized;

        public NativeApi()
        {
            try
            {
                _library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
                var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
                _initialize = Get<NoArg>(0x0150E828);
                _unload = Get<NoArg>(0xD22BDD7E);
                _enumerate = Get<EnumGpus>(0xE5AC921F);
                _getDisplays = Get<GetDisplayIds>(0x0078DBA2);
                T Get<T>(uint id) where T : Delegate
                {
                    nint address = query(id);
                    if (address == 0) throw new EntryPointNotFoundException($"NVAPI QueryInterface 0x{id:X8}");
                    return Marshal.GetDelegateForFunctionPointer<T>(address);
                }
            }
            catch { if (_library != 0) NativeLibrary.Free(_library); _library = 0; throw; }
        }

        public int Initialize() { int status = _initialize(); _initialized = status == 0; return status; }
        public int Enumerate(nint[] handles, out int count) => _enumerate(handles, out count);
        public int GetDisplays(nint gpu, NvGpuDisplayId[]? displays, ref uint count)
        {
            if (displays is null) return _getDisplays(gpu, 0, ref count, 1);
            int size = Marshal.SizeOf<NvGpuDisplayId>();
            nint buffer = Marshal.AllocHGlobal(checked(size * displays.Length));
            try
            {
                for (int i = 0; i < displays.Length; i++) Marshal.StructureToPtr(displays[i], buffer + i * size, false);
                int status = _getDisplays(gpu, buffer, ref count, 1 /* UNCACHED */);
                // The returned count may grow on a hot-plug error. Never ask the
                // marshaler to copy that many records from the original buffer.
                if (status == 0 && count <= displays.Length)
                    for (int i = 0; i < count; i++) displays[i] = Marshal.PtrToStructure<NvGpuDisplayId>(buffer + i * size);
                return status;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose()
        {
            if (_library == 0) return;
            try { if (_initialized) _unload(); }
            finally { NativeLibrary.Free(_library); _library = 0; }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint QueryInterface(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArg();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumGpus([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] nint[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDisplayIds(nint gpu, nint displays, ref uint count, uint flags);
    }
}
