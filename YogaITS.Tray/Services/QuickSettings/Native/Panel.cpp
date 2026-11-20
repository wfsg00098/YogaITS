#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <algorithm>
#include <atomic>
#include <memory>
#include <string_view>
#include <vector>

namespace xaml = winrt::Windows::UI::Xaml;
namespace controls = xaml::Controls;
namespace media = xaml::Media;
namespace wf = winrt::Windows::Foundation;
namespace shapes = xaml::Shapes;

namespace {
constexpr CLSID TapClass{0xa3b07347,0xa74a,0x4486,{0xa3,0x97,0x2d,0x71,0x59,0x4b,0x96,0x5a}};
constexpr auto Owner = L"Local\\YogaITS.QuickSettings.Owner";
constexpr auto Enabled = L"Local\\YogaITS.QuickSettings.Enabled";
constexpr auto Ready = L"Local\\YogaITS.QuickSettings.Ready";
constexpr auto Left = L"Local\\YogaITS.QuickSettings.Left";
constexpr auto Right = L"Local\\YogaITS.QuickSettings.Right";
constexpr auto Tag = L"YogaITS.QuickSettings.Button";
constexpr unsigned RefreshMessage = WM_APP + 1;
constexpr double Gap = 5;
constexpr double GlyphSize = 16;
HMODULE module;
std::atomic<bool> subscribed;

bool ReadSignal(PCWSTR name) noexcept
{
    HANDLE event = OpenEventW(SYNCHRONIZE, FALSE, name);
    if (!event) return false;
    bool signaled = WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
    CloseHandle(event);
    return signaled;
}

void Publish(PCWSTR name, bool value = true) noexcept
{
    HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
    if (!event) return;
    if (value) SetEvent(event);
    else ResetEvent(event);
    CloseHandle(event);
}

void DismissActiveFlyout() noexcept
{
    HWND flyout = GetForegroundWindow();
    if (!flyout || GetWindowThreadProcessId(flyout, nullptr) != GetCurrentThreadId()) return;

    // Only the foreground flyout on this XAML thread receives its normal
    // Escape dismissal. Do not send input to the new YogaITS window.
    HWND focus = GetFocus();
    HWND target = focus && GetAncestor(focus, GA_ROOT) == flyout ? focus : flyout;
    auto key = static_cast<LPARAM>(1u | (MapVirtualKeyW(VK_ESCAPE, MAPVK_VK_TO_VSC) << 16));
    if (PostMessageW(target, WM_KEYDOWN, VK_ESCAPE, key))
        PostMessageW(target, WM_KEYUP, VK_ESCAPE, key | 0xC0000000);
}

void OpenWindow(PCWSTR action) noexcept
{
    if (!ReadSignal(Owner) || !ReadSignal(Enabled) || !ReadSignal(Ready)) return;
    DWORD pid = 0;
    if (HWND owner = FindWindowW(nullptr, L"YogaITS.QuickSettings.Owner"))
        GetWindowThreadProcessId(owner, &pid);
    if (pid) AllowSetForegroundWindow(pid);
    DismissActiveFlyout();
    Publish(action);
}

bool IsControlCenter(controls::Grid const& grid)
{
    xaml::DependencyObject parent = grid;
    for (int depth = 0; parent && depth < 24; ++depth)
    {
        if (std::wstring_view(winrt::get_class_name(parent)).find(L"ControlCenter") != std::wstring_view::npos)
            return true;
        parent = media::VisualTreeHelper::GetParent(parent);
    }
    return false;
}

template<typename Visitor>
bool Visit(xaml::DependencyObject const& node, Visitor const& visit, int depth = 0)
{
    if (depth > 22) return false;
    if (auto element = node.try_as<xaml::FrameworkElement>())
    {
        if (element.Visibility() != xaml::Visibility::Visible) return false;
        if (visit(element)) return true;
    }
    int count = media::VisualTreeHelper::GetChildrenCount(node);
    for (int i = 0; i < count; ++i)
        if (Visit(media::VisualTreeHelper::GetChild(node, i), visit, depth + 1)) return true;
    return false;
}

wf::Rect Bounds(xaml::FrameworkElement const& element, controls::Grid const& grid)
{
    return element.TransformToVisual(grid).TransformBounds(
        {0, 0, static_cast<float>(element.ActualWidth()), static_cast<float>(element.ActualHeight())});
}

bool Overlaps(wf::Rect a, wf::Rect b)
{
    return a.X < b.X + b.Width && b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}

shapes::Path CreateGlyph()
{
    media::EllipseGeometry ring;
    ring.Center({8, 8});
    ring.RadiusX(6.5);
    ring.RadiusY(6.5);

    media::GeometryGroup outline;
    outline.Children().Append(ring);
    auto line = [&](wf::Point start, wf::Point end)
    {
        media::LineGeometry segment;
        segment.StartPoint(start);
        segment.EndPoint(end);
        outline.Children().Append(segment);
    };
    line({5, 5.7f}, {8, 8.2f});
    line({11, 5.7f}, {8, 8.2f});
    line({8, 8.2f}, {8, 11.4f});

    shapes::Path glyph;
    glyph.Data(outline);
    glyph.Stretch(media::Stretch::None);
    glyph.StrokeThickness(1);
    glyph.StrokeStartLineCap(media::PenLineCap::Round);
    glyph.StrokeEndLineCap(media::PenLineCap::Round);
    return glyph;
}

xaml::FrameworkElement GearGlyph(controls::Button const& settings)
{
    if (auto content = settings.Content().try_as<controls::IconElement>()) return content;
    xaml::FrameworkElement result{nullptr};
    Visit(settings, [&](xaml::FrameworkElement const& element)
    {
        if (element.Name() != L"FooterButtonIcon") return false;
        result = element;
        return true;
    });
    return result;
}

struct Footer
{
    winrt::weak_ref<controls::Grid> grid;
    controls::Grid slotHost{nullptr};
    controls::Button button{nullptr};
    controls::Viewbox icon{nullptr};
    shapes::Path glyph{nullptr};
    media::TranslateTransform iconOffset{nullptr};
    media::SolidColorBrush fallbackInk{nullptr};

    void Remove()
    {
        if (!slotHost) return;
        if (auto root = grid.get())
        {
            uint32_t index;
            if (root.Children().IndexOf(slotHost, index)) root.Children().RemoveAt(index);
        }
        slotHost = nullptr;
        button = nullptr;
        icon = nullptr;
        glyph = nullptr;
        iconOffset = nullptr;
        fallbackInk = nullptr;
    }

    bool Refresh(controls::Grid const& root, controls::Button const& settings)
    {
        auto bounds = Bounds(settings, root);
        wf::Rect slot{static_cast<float>(bounds.X - bounds.Width - Gap), bounds.Y, bounds.Width, bounds.Height};
        if (slot.X < 0 || slot.Width <= 0 || slot.Height <= 0)
        {
            Remove();
            return false;
        }
        bool occupied = Visit(root, [&](xaml::FrameworkElement const& element)
        {
            auto control = element.try_as<controls::Primitives::ButtonBase>();
            return control && control != settings && control != button && control.IsHitTestVisible() &&
                control.ActualWidth() > 0 && control.ActualHeight() > 0 && Overlaps(slot, Bounds(control, root));
        });
        if (occupied)
        {
            Remove();
            return false;
        }
        if (!button)
        {
            for (auto const& child : root.Children())
                if (auto element = child.try_as<xaml::FrameworkElement>(); element &&
                    winrt::unbox_value_or<winrt::hstring>(element.Tag(), L"") == Tag) return false;
            slotHost = controls::Grid();
            slotHost.Tag(winrt::box_value(Tag));
            slotHost.HorizontalAlignment(xaml::HorizontalAlignment::Left);
            slotHost.VerticalAlignment(xaml::VerticalAlignment::Top);
            button = controls::Button();
            // Reuse the shell button for interaction. Keep the glyph outside
            // its content presenter so its padding and clip cannot cut the ring.
            glyph = CreateGlyph();
            controls::Canvas canvas;
            canvas.Width(GlyphSize);
            canvas.Height(GlyphSize);
            canvas.Children().Append(glyph);
            icon = controls::Viewbox();
            icon.Child(canvas);
            icon.Stretch(media::Stretch::Uniform);
            icon.HorizontalAlignment(xaml::HorizontalAlignment::Center);
            icon.VerticalAlignment(xaml::VerticalAlignment::Center);
            iconOffset = media::TranslateTransform();
            icon.RenderTransform(iconOffset);
            icon.IsHitTestVisible(false);
            fallbackInk = media::SolidColorBrush();
            button.IsEnabled(false);
            xaml::Automation::AutomationProperties::SetName(button, L"Yoga控制中心");
            xaml::Automation::AutomationProperties::SetAutomationId(button, Tag);
            controls::ToolTipService::SetToolTip(button, winrt::box_value(L"Yoga控制中心"));
            button.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
            button.VerticalAlignment(xaml::VerticalAlignment::Stretch);
            button.Click([](auto&&, auto&&) { OpenWindow(Left); });
            button.RightTapped([](auto&&, xaml::Input::RightTappedRoutedEventArgs const& args)
            {
                args.Handled(true);
                OpenWindow(Right);
            });
            slotHost.Children().Append(button);
            slotHost.Children().Append(icon);
            controls::Grid::SetColumnSpan(slotHost, std::max(1u, root.ColumnDefinitions().Size()));
            controls::Grid::SetRowSpan(slotHost, std::max(1u, root.RowDefinitions().Size()));
            root.Children().Append(slotHost);
        }
        if (button.Style() != settings.Style()) button.Style(settings.Style());
        if (button.Template() != settings.Template()) button.Template(settings.Template());
        if (button.BorderThickness() != settings.BorderThickness()) button.BorderThickness(settings.BorderThickness());
        if (button.CornerRadius() != settings.CornerRadius()) button.CornerRadius(settings.CornerRadius());
        if (button.Background() != settings.Background()) button.Background(settings.Background());
        if (button.BorderBrush() != settings.BorderBrush()) button.BorderBrush(settings.BorderBrush());
        xaml::Thickness position{slot.X, slot.Y, 0, 0};
        if (slotHost.Margin() != position) slotHost.Margin(position);
        if (slotHost.Width() != slot.Width) slotHost.Width(slot.Width);
        if (slotHost.Height() != slot.Height) slotHost.Height(slot.Height);
        auto gear = GearGlyph(settings);
        double size = GlyphSize;
        double x = 0, y = 0;
        if (gear && gear.ActualWidth() > 0 && gear.ActualHeight() > 0)
        {
            auto anchor = Bounds(gear, root);
            size = std::min(anchor.Width, anchor.Height);
            x = anchor.X + anchor.Width / 2.0 - bounds.X - bounds.Width / 2.0;
            // The gear's visible stroke sits 1 DIP above its icon viewport center.
            y = anchor.Y + anchor.Height / 2.0 - bounds.Y - bounds.Height / 2.0 - 1;
        }
        if (icon.Width() != size) icon.Width(size);
        if (icon.Height() != size) icon.Height(size);
        if (iconOffset.X() != x) iconOffset.X(x);
        if (iconOffset.Y() != y) iconOffset.Y(y);
        media::Brush ink{nullptr};
        if (auto nativeIcon = gear.try_as<controls::IconElement>()) ink = nativeIcon.Foreground();
        if (!ink) ink = settings.Foreground();
        if (!ink)
        {
            auto color = settings.ActualTheme() == xaml::ElementTheme::Dark
                ? winrt::Windows::UI::Colors::White() : winrt::Windows::UI::Colors::Black();
            if (fallbackInk.Color() != color) fallbackInk.Color(color);
            ink = fallbackInk;
        }
        if (glyph.Stroke() != ink) glyph.Stroke(ink);
        bool ready = button.IsLoaded() && button.ActualWidth() > 0 && icon.ActualWidth() > 0;
        if (button.IsEnabled() != ready) button.IsEnabled(ready);
        return ready;
    }
};

struct Context
{
    HWND window{};
    std::vector<Footer> footers;
    static thread_local Context* current;

    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept
    {
        auto context = reinterpret_cast<Context*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (context && (message == WM_TIMER || message == RefreshMessage))
        {
            try { context->Refresh(); }
            catch (...)
            {
                Publish(Ready, false);
                OutputDebugStringW(L"YogaITS: Quick Settings footer refresh failed.\n");
            }
            return 0;
        }
        if (context && message == WM_NCDESTROY)
        {
            current = nullptr;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            delete context;
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    static void Observe(controls::Grid const& grid)
    {
        if (!current)
        {
            WNDCLASSEXW cls{sizeof(cls)};
            cls.hInstance = module;
            cls.lpfnWndProc = WindowProc;
            wchar_t name[100];
            swprintf_s(name, L"YogaITS.QuickSettings.Xaml.%p", static_cast<void*>(module));
            cls.lpszClassName = name;
            RegisterClassExW(&cls);
            auto context = std::make_unique<Context>();
            HWND hwnd = CreateWindowExW(0, name, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, module, nullptr);
            if (!hwnd) return;
            if (!SetTimer(hwnd, 1, 250, nullptr))
            {
                DestroyWindow(hwnd);
                return;
            }
            current = context.release();
            current->window = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(current));
        }
        for (auto const& footer : current->footers) if (footer.grid.get() == grid) return;
        current->footers.push_back({ winrt::make_weak(grid) });
        PostMessageW(current->window, RefreshMessage, 0, 0);
    }

    void Refresh()
    {
        bool alive = ReadSignal(Owner);
        if (!alive || !ReadSignal(Enabled))
        {
            for (auto& footer : footers) footer.Remove();
            if (!alive) DestroyWindow(window);
            return;
        }
        std::erase_if(footers, [](auto const& footer) { return !footer.grid.get(); });
        bool measured = false, mounted = false;
        for (auto& footer : footers)
        {
            auto grid = footer.grid.get();
            if (!grid || !grid.XamlRoot() || !grid.XamlRoot().IsHostVisible() || grid.ActualWidth() <= 0 || !IsControlCenter(grid))
                continue;
            controls::Button settings{nullptr};
            Visit(grid, [&](xaml::FrameworkElement const& element)
            {
                if (element.Name() == L"FooterButton") settings = element.try_as<controls::Button>();
                return static_cast<bool>(settings);
            });
            measured = true;
            if (settings) mounted |= footer.Refresh(grid, settings);
            else footer.Remove();
        }
        // Closing the flyout keeps confirmed availability. A visible but
        // incompatible footer returns the app to the tray.
        if (measured) Publish(Ready, mounted);
    }
};
thread_local Context* Context::current;

struct Tap : winrt::implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
{
    winrt::com_ptr<IUnknown> site;
    winrt::com_ptr<IXamlDiagnostics> diagnostics;
    std::atomic<bool> stopped;

    HRESULT __stdcall SetSite(IUnknown* value) noexcept override
    {
        if (!value) { stopped = true; return S_OK; }
        try
        {
            if (subscribed.exchange(true)) return S_OK;
            site.copy_from(value);
            diagnostics = site.as<IXamlDiagnostics>();
            auto context = std::make_unique<winrt::com_ptr<Tap>>(get_strong());
            HANDLE thread = CreateThread(nullptr, 0, [](void* data) -> DWORD
            {
                std::unique_ptr<winrt::com_ptr<Tap>> self(static_cast<winrt::com_ptr<Tap>*>(data));
                HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                try
                {
                    auto service = (*self)->site.as<IVisualTreeService>();
                    winrt::check_hresult(service->AdviseVisualTreeChange(self->get()));
                    while (!(*self)->stopped && ReadSignal(Owner)) Sleep(250);
                    (*self)->stopped = true;
                    service->UnadviseVisualTreeChange(self->get());
                }
                catch (...) { OutputDebugStringW(L"YogaITS: Quick Settings diagnostics subscription failed.\n"); }
                subscribed = false;
                if (SUCCEEDED(initialized)) CoUninitialize();
                return 0;
            }, context.get(), CREATE_SUSPENDED, nullptr);
            if (!thread) winrt::throw_last_error();
            context.release();
            ResumeThread(thread);
            CloseHandle(thread);
            return S_OK;
        }
        catch (...) { subscribed = false; return winrt::to_hresult(); }
    }

    HRESULT __stdcall GetSite(REFIID iid, void** output) noexcept override
    {
        return site ? site->QueryInterface(iid, output) : E_FAIL;
    }

    HRESULT __stdcall OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override
    {
        if (stopped || mutation != Add || !element.Name || wcscmp(element.Name, L"FooterGrid") != 0) return S_OK;
        try
        {
            wf::IInspectable object{nullptr};
            winrt::check_hresult(diagnostics->GetIInspectableFromHandle(element.Handle,
                reinterpret_cast<::IInspectable**>(winrt::put_abi(object))));
            if (auto grid = object.try_as<controls::Grid>()) Context::Observe(grid);
        }
        catch (...) { OutputDebugStringW(L"YogaITS: Quick Settings grid observation failed.\n"); }
        return S_OK;
    }
    HRESULT __stdcall OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }
};

struct Factory : winrt::implements<Factory, IClassFactory>
{
    HRESULT __stdcall CreateInstance(IUnknown* outer, REFIID iid, void** output) noexcept override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try { return winrt::make_self<Tap>()->QueryInterface(iid, output); }
        catch (...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall LockServer(BOOL) noexcept override { return S_OK; }
};
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** output)
{
    if (!output) return E_POINTER;
    *output = nullptr;
    if (clsid != TapClass) return CLASS_E_CLASSNOTAVAILABLE;
    try { return winrt::make_self<Factory>()->QueryInterface(iid, output); }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    return TRUE;
}
