using System.Runtime.InteropServices;
using YogaITS.Tray.Services;

namespace YogaITS.dGPUKeepAwake;

// Only this process owns a D3D device. No swap chain, rendering, P-state lock,
// NVAPI telemetry, or synthetic GPU workload is needed to keep the context alive.
internal sealed unsafe class NvidiaIdleContext : IDisposable
{
    private nint _device;
    private nint _context;

    public bool IsUsable => GpuKeepAwakePolicy.IsDeviceUsable(_device != 0, _device == 0 ? 0 :
        ((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)_device)[39])(_device));

    public void Create()
    {
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out nint factory));
        try
        {
            for (uint index = 0; ; index++)
            {
                nint adapter = 0;
                int result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12])(factory, index, &adapter);
                if (result == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    AdapterDescription description;
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10])(adapter, &description));
                    if (!GpuKeepAwakePolicy.IsNvidiaHardware(description.VendorId, description.Flags)) continue;
                    // An explicit adapter requires D3D_DRIVER_TYPE_UNKNOWN, not
                    // HARDWARE/default-adapter selection (LLT's wrong-adapter fix).
                    int created = D3D11CreateDevice(adapter, 0, 0, 0x20, 0, 0, 7,
                        out _device, out _, out _context);
                    if (created >= 0) return;
                    Dispose(); // Another NVIDIA adapter may still be usable.
                }
                finally { Marshal.Release(adapter); }
            }
            throw new InvalidOperationException("No available NVIDIA hardware adapter.");
        }
        finally { Marshal.Release(factory); }
    }

    public void Dispose()
    {
        if (_context != 0) { Marshal.Release(_context); _context = 0; }
        if (_device != 0) { Marshal.Release(_device); _device = 0; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
}
