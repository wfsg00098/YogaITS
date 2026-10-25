#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/base.h>
#include <windows.ui.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.h>
#include <vector>
#include <cwchar>

// Thin device/ABI bridge only. Capture/lifetime/latest-frame policy lives in C#.
// Select the monitor's real adapter, not adapter zero or a forced NVIDIA device.
extern "C" __declspec(dllexport) HRESULT __stdcall CreateCaptureDevice(
    HMONITOR monitor, IInspectable** result, BOOL* hdr) noexcept
{
    if (!result || !hdr) return E_POINTER;
    *result = nullptr; *hdr = FALSE;
    try
    {
        winrt::com_ptr<IDXGIFactory1> factory;
        winrt::check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()));
        winrt::com_ptr<IDXGIAdapter1> selected;
        for (UINT index = 0; ; ++index)
        {
            winrt::com_ptr<IDXGIAdapter1> adapter;
            auto status = factory->EnumAdapters1(index, adapter.put());
            if (status == DXGI_ERROR_NOT_FOUND) break;
            winrt::check_hresult(status);
            for (UINT outputIndex = 0; ; ++outputIndex)
            {
                winrt::com_ptr<IDXGIOutput> output;
                status = adapter->EnumOutputs(outputIndex, output.put());
                if (status == DXGI_ERROR_NOT_FOUND) break;
                winrt::check_hresult(status);
                DXGI_OUTPUT_DESC desc{}; winrt::check_hresult(output->GetDesc(&desc));
                if (desc.Monitor != monitor) continue;
                selected = adapter;
                if (auto advanced = output.try_as<IDXGIOutput6>())
                {
                    DXGI_OUTPUT_DESC1 details{};
                    if (SUCCEEDED(advanced->GetDesc1(&details)))
                        *hdr = details.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                }
                break;
            }
            if (selected) break;
        }
        if (!selected) return DXGI_ERROR_NOT_FOUND;
        winrt::com_ptr<ID3D11Device> device;
        winrt::com_ptr<ID3D11DeviceContext> context;
        winrt::check_hresult(D3D11CreateDevice(selected.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            device.put(), nullptr, context.put()));
        (void)context.as<ID3D11Multithread>()->SetMultithreadProtected(TRUE);
        return CreateDirect3D11DeviceFromDXGIDevice(device.as<IDXGIDevice>().get(), result);
    }
    catch (...) { return winrt::to_hresult(); }
}

// Submit the GPU bitmap copy before its WGC source buffer is returned to the
// pool. Flush submits commands; it is not a CPU wait or a pixel readback.
extern "C" __declspec(dllexport) HRESULT __stdcall FlushCaptureDevice(IInspectable* source) noexcept
{
    if (!source) return E_POINTER;
    try
    {
        winrt::com_ptr<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess> access;
        winrt::check_hresult(source->QueryInterface(__uuidof(::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess), access.put_void()));
        winrt::com_ptr<ID3D11Device> device;
        winrt::check_hresult(access->GetInterface(__uuidof(ID3D11Device), device.put_void()));
        winrt::com_ptr<ID3D11DeviceContext> context;
        device->GetImmediateContext(context.put());
        context->Flush();
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

// Read-only display configuration query. Never change HDR, ACM or brightness.
extern "C" __declspec(dllexport) HRESULT __stdcall ReadCaptureSdrWhiteScale(HMONITOR monitor, float* scale) noexcept
{
    if (!scale) return E_POINTER;
    *scale = 1; // Explicit 80-nit fallback, reported by the managed caller.
    try
    {
        MONITORINFOEXW info{}; info.cbSize = sizeof(info);
        if (!GetMonitorInfoW(monitor, &info)) return HRESULT_FROM_WIN32(GetLastError());
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            UINT32 pathCount{}, modeCount{};
            LONG status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
            if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
            std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
            std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
            status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(), nullptr);
            if (status == ERROR_INSUFFICIENT_BUFFER) continue;
            if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
            for (UINT32 index = 0; index < pathCount; ++index)
            {
                auto const& path = paths[index];
                DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = sizeof(source);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS ||
                    _wcsicmp(source.viewGdiDeviceName, info.szDevice) != 0) continue;
                DISPLAYCONFIG_SDR_WHITE_LEVEL white{};
                white.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                white.header.size = sizeof(white);
                white.header.adapterId = path.targetInfo.adapterId;
                white.header.id = path.targetInfo.id;
                status = DisplayConfigGetDeviceInfo(&white.header);
                if (status != ERROR_SUCCESS) continue;
                if (white.SDRWhiteLevel < 100 || white.SDRWhiteLevel > 100000) return E_UNEXPECTED;
                *scale = static_cast<float>(white.SDRWhiteLevel) / 1000.0f;
                return S_OK;
            }
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }
    catch (...) { return winrt::to_hresult(); }
}

// SDK-projected public session API; no DWM hooks/injection or private GUIDs.
// Configure before StartCapture, then update in place. HWNDs use Windows.UI interop,
// rather than assuming Microsoft.UI/Windows.UI WindowId values are identical.
extern "C" __declspec(dllexport) HRESULT __stdcall SetCaptureWindowExclusions(
    IInspectable* source, HWND const* windows, UINT32 count, UINT64* iteration) noexcept
{
    if (!source || !iteration || (count && !windows)) return E_POINTER;
    *iteration = 0;
    try
    {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, source);
        auto display = object.as<winrt::Windows::Graphics::Capture::IDisplayGraphicsCaptureSession>();
        // The SDK declares this Windows.UI conversion, but supplies no import
        // library in this kit. Resolve its confirmed system DLL export only.
        static HMODULE const windowsUI = LoadLibraryExW(L"Windows.UI.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!windowsUI) return HRESULT_FROM_WIN32(GetLastError());
        using ConvertWindowId = HRESULT(WINAPI*)(HWND, ABI::Windows::UI::WindowId*);
        auto convert = reinterpret_cast<ConvertWindowId>(GetProcAddress(windowsUI, "GetWindowIdFromWindow"));
        if (!convert) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        std::vector<winrt::Windows::UI::WindowId> ids;
        for (UINT32 index = 0; index < count; ++index)
        {
            if (!IsWindow(windows[index])) continue; // A closing HWND may already be destroyed.
            ABI::Windows::UI::WindowId id{};
            auto status = convert(windows[index], &id);
            if (FAILED(status) && !IsWindow(windows[index])) continue; // Close raced conversion.
            winrt::check_hresult(status);
            ids.push_back({ id.Value });
        }
        auto excluded = winrt::single_threaded_vector<winrt::Windows::UI::WindowId>(std::move(ids));
        *iteration = display.SetWindowExclusionList(excluded);
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __stdcall ReadCaptureFrameIteration(
    IInspectable* source, UINT64* iteration) noexcept
{
    if (!source || !iteration) return E_POINTER;
    *iteration = 0;
    try
    {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, source);
        *iteration = object.as<winrt::Windows::Graphics::Capture::IDirect3D11CaptureFrame3>().ConfigurationIteration();
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

// Avoid the observed Windows 24H2+ default/zero-interval ~60-Hz capture path.
// A 1-ms minimum is a high-rate request, not a promise of 1000 actual FPS.
extern "C" __declspec(dllexport) HRESULT __stdcall ConfigureCaptureTiming(
    IInspectable* source, HMONITOR monitor, float* hz, float* before, float* after) noexcept
{
    if (!source || !hz || !before || !after) return E_POINTER;
    *hz = 0; *before = *after = -1;
    try
    {
        MONITORINFOEXW info{}; info.cbSize = sizeof(info);
        DEVMODEW mode{}; mode.dmSize = sizeof(mode);
        if (GetMonitorInfoW(monitor, &info) && EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode))
            *hz = static_cast<float>(mode.dmDisplayFrequency);
        winrt::Windows::Foundation::IInspectable object{nullptr}; winrt::copy_from_abi(object, source);
        auto session = object.as<winrt::Windows::Graphics::Capture::GraphicsCaptureSession>();
        *before = static_cast<float>(session.MinUpdateInterval().count()) / 10000.0f;
        session.MinUpdateInterval(std::chrono::milliseconds(1));
        *after = static_cast<float>(session.MinUpdateInterval().count()) / 10000.0f;
        return *after == 1.0f ? S_OK : E_UNEXPECTED;
    }
    catch (...) { return winrt::to_hresult(); }
}