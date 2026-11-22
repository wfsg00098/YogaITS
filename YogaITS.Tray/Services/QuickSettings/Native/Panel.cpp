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
#include <mutex>
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
constexpr auto OwnerWindowTitle = L"YogaITS.QuickSettings.Owner";
constexpr auto ControllerProperty = L"YogaITS.QuickSettings.Controller";
constexpr auto ContextProperty = L"YogaITS.QuickSettings.Context";
constexpr auto MountedProperty = L"YogaITS.QuickSettings.Mounted";
constexpr auto Tag = L"YogaITS.QuickSettings.Button";
constexpr auto TooltipText = L"Yoga控制中心";
// Message IDs and reply values are shared with QuickSettingsHost/Recovery.
constexpr unsigned RefreshMessage = WM_APP + 1;
constexpr unsigned QueryStateMessage = WM_APP + 2;
constexpr unsigned RecycleMessage = WM_APP + 3;
enum class BridgeState { Missing = 1, Waiting = 2, Ready = 3 };
constexpr double Gap = 5;
constexpr double GlyphSize = 16;
// The static outline has more fully covered pixels than the animated gear.
// Keep the shared theme brush and soften the composited stroke slightly.
constexpr double GlyphOpacity = 0.93;
HMODULE module;

struct Watch
{
    HWND controller{};
    std::atomic<bool> stopped{};
};
std::atomic<std::shared_ptr<Watch>> latestWatch;
std::mutex watchMutex;

bool ReadSignal(PCWSTR name) noexcept
{
    HANDLE event = OpenEventW(SYNCHRONIZE, FALSE, name);
    if (!event) return false;
    bool signaled = WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
    CloseHandle(event);
    return signaled;
}

void Publish(PCWSTR name) noexcept
{
    HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
    if (!event) return;
    SetEvent(event);
    CloseHandle(event);
}

bool ControllerActive(HWND controller) noexcept
{
    return controller && GetPropW(controller, ControllerProperty) == controller && ReadSignal(Owner);
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

void OpenWindow(HWND bridge, PCWSTR action) noexcept
{
    auto controller = static_cast<HWND>(GetPropW(bridge, MountedProperty));
    if (!ControllerActive(controller) || !ReadSignal(Enabled)) return;
    DWORD pid = 0;
    GetWindowThreadProcessId(controller, &pid);
    if (pid) AllowSetForegroundWindow(pid);
    DismissActiveFlyout();
    Publish(action);
}

void AttachTooltip(controls::Button const& button)
{
    controls::ToolTip tooltip;
    tooltip.Content(winrt::box_value(TooltipText));
    controls::ToolTipService::SetToolTip(button, tooltip);
}

void DismissTooltip(controls::Button const& button)
{
    if (auto tooltip = controls::ToolTipService::GetToolTip(button).try_as<controls::ToolTip>())
    {
        tooltip.IsOpen(false);
        controls::ToolTipService::SetToolTip(button, nullptr);
    }
}

void BindButtonActions(controls::Button const& button, HWND bridge)
{
    AttachTooltip(button);
    auto clickPosition = std::make_shared<POINT>();
    auto rearm = [clickPosition](wf::IInspectable const& sender, auto const&)
    {
        auto target = sender.as<controls::Button>();
        if (controls::ToolTipService::GetToolTip(target)) return;
        auto root = target.XamlRoot();
        POINT current{};
        if (root && root.IsHostVisible() && GetCursorPos(&current) &&
            (current.x != clickPosition->x || current.y != clickPosition->y))
            AttachTooltip(target);
    };
    button.PointerEntered(rearm);
    button.PointerExited(rearm);

    auto activate = [clickPosition, bridge](wf::IInspectable const& sender, PCWSTR action)
    {
        // End the current tooltip session before the flyout loses focus. A
        // stationary pointer must not rearm it during the shell close animation.
        GetCursorPos(clickPosition.get());
        DismissTooltip(sender.as<controls::Button>());
        OpenWindow(bridge, action);
    };
    button.Click([activate](wf::IInspectable const& sender, auto const&) { activate(sender, Left); });
    button.RightTapped([activate](wf::IInspectable const& sender, xaml::Input::RightTappedRoutedEventArgs const& args)
    {
        args.Handled(true);
        activate(sender, Right);
    });
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

wf::Rect FindFooterSlot(controls::Grid const& root, controls::Button const& settings,
    controls::Button const& button, wf::Rect bounds)
{
    double left = bounds.X - bounds.Width - Gap;
    std::vector<wf::Rect> blockers;
    Visit(root, [&](xaml::FrameworkElement const& element)
    {
        bool oem = element.Name() == L"OEMButton";
        auto control = element.try_as<controls::Primitives::ButtonBase>();
        if (element == settings || element == button || (!oem && (!control || !control.IsHitTestVisible())) ||
            element.ActualWidth() <= 0 || element.ActualHeight() <= 0) return false;
        auto other = Bounds(element, root);
        if (other.X < bounds.X && other.Y < bounds.Y + bounds.Height && bounds.Y < other.Y + other.Height)
        {
            if (oem) left = std::min(left, other.X - bounds.Width - Gap);
            blockers.push_back(other);
        }
        return false;
    });
    // Walk left from the gear so native OEM launchers and other footer buttons
    // keep their spacing. Reuse the nearest gap that fits our full button.
    std::ranges::sort(blockers, std::ranges::greater{}, &wf::Rect::X);
    for (auto const& other : blockers)
        if (left < other.X + other.Width + Gap && other.X < left + bounds.Width + Gap)
            left = other.X - bounds.Width - Gap;
    return {static_cast<float>(left), bounds.Y, bounds.Width, bounds.Height};
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

void RequestRefresh(HWND window) noexcept;

struct LayoutEvents
{
    xaml::FrameworkElement::Loaded_revoker loaded;
    xaml::FrameworkElement::Unloaded_revoker unloaded;
    xaml::FrameworkElement::LayoutUpdated_revoker layout;

    LayoutEvents(xaml::FrameworkElement const& element, HWND window)
    {
        auto refresh = [window](auto const&, auto const&) { RequestRefresh(window); };
        loaded = element.Loaded(winrt::auto_revoke, refresh);
        unloaded = element.Unloaded(winrt::auto_revoke, refresh);
        layout = element.LayoutUpdated(winrt::auto_revoke, refresh);
    }
};

struct Footer
{
    winrt::weak_ref<controls::Grid> grid;
    LayoutEvents events;
    controls::Grid slotHost{nullptr};
    controls::Button button{nullptr};
    controls::Viewbox icon{nullptr};
    shapes::Path glyph{nullptr};
    media::TranslateTransform iconOffset{nullptr};
    media::SolidColorBrush fallbackInk{nullptr};

    void Remove() noexcept
    {
        if (!slotHost) return;
        try { if (button) DismissTooltip(button); }
        catch (...) {}
        try
        {
            if (auto root = grid.get())
            {
                uint32_t index;
                if (root.Children().IndexOf(slotHost, index)) root.Children().RemoveAt(index);
            }
        }
        catch (...) {}
        slotHost = nullptr;
        button = nullptr;
        icon = nullptr;
        glyph = nullptr;
        iconOffset = nullptr;
        fallbackInk = nullptr;
    }

    bool IsMounted(controls::Grid const& root) const
    {
        uint32_t index;
        return slotHost && button.IsEnabled() && root.Children().IndexOf(slotHost, index);
    }

    bool Refresh(controls::Grid const& root, controls::Button const& settings, HWND bridge)
    {
        auto bounds = Bounds(settings, root);
        auto slot = FindFooterSlot(root, settings, button, bounds);
        if (slot.X < 0 || slot.Width <= 0 || slot.Height <= 0)
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
            icon.Opacity(GlyphOpacity);
            fallbackInk = media::SolidColorBrush();
            button.IsEnabled(false);
            xaml::Automation::AutomationProperties::SetName(button, TooltipText);
            xaml::Automation::AutomationProperties::SetAutomationId(button, Tag);
            button.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
            button.VerticalAlignment(xaml::VerticalAlignment::Stretch);
            BindButtonActions(button, bridge);
            slotHost.Children().Append(button);
            slotHost.Children().Append(icon);
        }
        auto children = root.Children();
        uint32_t index;
        if (!children.IndexOf(slotHost, index))
        {
            controls::Grid::SetColumnSpan(slotHost, std::max(1u, root.ColumnDefinitions().Size()));
            controls::Grid::SetRowSpan(slotHost, std::max(1u, root.RowDefinitions().Size()));
            children.Append(slotHost);
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
    struct Page
    {
        winrt::weak_ref<xaml::FrameworkElement> element;
        LayoutEvents events;
    };

    HWND window{};
    std::shared_ptr<Watch> watch;
    std::vector<Page> pages;
    std::vector<Footer> footers;
    BridgeState state = BridgeState::Missing;
    std::wstring_view reason;
    bool refreshQueued{};
    bool enabled{};
    static thread_local Context* current;

    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept
    {
        auto context = reinterpret_cast<Context*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (context && (message == WM_TIMER || message == RefreshMessage ||
            message == QueryStateMessage || message == RecycleMessage))
        {
            if (!ControllerActive(context->watch->controller))
            {
                // Do not tear down XAML in the synchronous state query.
                if (message == QueryStateMessage) RequestRefresh(hwnd);
                else context->Close();
                return 0;
            }
            if (message == QueryStateMessage || message == RecycleMessage ||
                (message == RefreshMessage && wparam != 0))
            {
                if (wparam != reinterpret_cast<WPARAM>(context->watch->controller)) return 0;
                if (message != RecycleMessage)
                {
                    RequestRefresh(hwnd);
                    return message == QueryStateMessage ? static_cast<LRESULT>(context->state) : 0;
                }
                context->RefreshSafely();
                if (context->state != BridgeState::Ready && ReadSignal(Enabled)) context->Close();
                return 0;
            }
            if (message == WM_TIMER && context->enabled == ReadSignal(Enabled)) return 0;
            if (message == RefreshMessage) context->refreshQueued = false;
            context->RefreshSafely();
            return 0;
        }
        if (context && message == WM_CLOSE)
        {
            context->Close();
            return 0;
        }
        if (context && message == WM_NCDESTROY)
        {
            RemovePropW(hwnd, ContextProperty);
            RemovePropW(hwnd, MountedProperty);
            current = nullptr;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            delete context;
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    static void Observe(xaml::FrameworkElement const& element, bool isFooter, std::shared_ptr<Watch> const& watch)
    {
        if (current && current->watch->controller != watch->controller) current->Close();
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
            if (!SetPropW(hwnd, ContextProperty, watch->controller) || !SetTimer(hwnd, 1, 250, nullptr))
            {
                RemovePropW(hwnd, ContextProperty);
                DestroyWindow(hwnd);
                return;
            }
            context->window = hwnd;
            context->watch = watch;
            current = context.release();
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(current));
            Publish(Ready);
        }
        current->watch = watch;
        if (isFooter)
        {
            if (auto grid = element.try_as<controls::Grid>()) current->AddFooter(grid);
        }
        else if (std::ranges::none_of(current->pages, [&](auto const& page) { return page.element.get() == element; }))
            current->pages.push_back({winrt::make_weak(element), LayoutEvents(element, current->window)});
        RequestRefresh(current->window);
    }

    void AddFooter(controls::Grid const& grid)
    {
        if (std::ranges::none_of(footers, [&](auto const& footer) { return footer.grid.get() == grid; }))
            footers.push_back({winrt::make_weak(grid), LayoutEvents(grid, window)});
    }

    void RefreshSafely() noexcept
    {
        try { Refresh(); }
        catch (...) { SetState(BridgeState::Missing, L"footer refresh failed"); }
    }

    void Refresh()
    {
        enabled = ReadSignal(Enabled);
        if (!enabled)
        {
            for (auto& footer : footers) footer.Remove();
            SetState(BridgeState::Missing, L"disabled");
            return;
        }

        std::erase_if(pages, [](auto const& page) { return !page.element.get(); });
        std::erase_if(footers, [](auto const& footer) { return !footer.grid.get(); });
        auto nextState = BridgeState::Missing;
        PCWSTR nextReason = L"no connected ControlCenter page or footer";
        for (auto const& candidate : pages)
        {
            auto page = candidate.element.get();
            auto root = page ? page.XamlRoot() : nullptr;
            if (!root) continue;
            nextState = BridgeState::Waiting;
            nextReason = L"ControlCenter page is waiting for its footer";
            if (!root.IsHostVisible()) continue;
            Visit(page, [&](xaml::FrameworkElement const& element)
            {
                if (element.Name() == L"FooterGrid")
                    if (auto grid = element.try_as<controls::Grid>()) AddFooter(grid);
                return false;
            });
        }

        bool mounted = false;
        for (auto& footer : footers)
        {
            auto grid = footer.grid.get();
            if (!grid) continue;
            auto root = grid.XamlRoot();
            if (!root || !IsControlCenter(grid))
            {
                footer.Remove();
                continue;
            }
            nextState = BridgeState::Waiting;
            nextReason = L"footer is waiting for layout";
            if (!grid.IsLoaded())
            {
                footer.Remove();
                continue;
            }
            if (!root.IsHostVisible())
            {
                mounted |= footer.IsMounted(grid);
                continue;
            }
            if (grid.ActualWidth() <= 0 || grid.ActualHeight() <= 0) continue;
            controls::Button settings{nullptr};
            Visit(grid, [&](xaml::FrameworkElement const& element)
            {
                if (element.Name() == L"FooterButton") settings = element.try_as<controls::Button>();
                return static_cast<bool>(settings);
            });
            nextReason = L"footer is waiting for the settings anchor";
            if (!settings)
            {
                footer.Remove();
                continue;
            }

            nextReason = L"footer is waiting for a usable button slot";
            mounted |= footer.Refresh(grid, settings, window);
        }
        if (mounted) SetState(BridgeState::Ready, L"button mounted");
        else SetState(nextState, nextReason);
    }

    void SetState(BridgeState value, PCWSTR detail) noexcept
    {
        if (state != value || reason != detail)
        {
            state = value;
            reason = detail;
            wchar_t message[200];
            swprintf_s(message, L"YogaITS: Quick Settings UI %d, %s.\n", static_cast<int>(state), detail);
            OutputDebugStringW(message);
        }
        HWND controller = value == BridgeState::Ready ? watch->controller : nullptr;
        if (GetPropW(window, MountedProperty) == controller) return;
        if (controller && !SetPropW(window, MountedProperty, controller)) return;
        if (!controller) RemovePropW(window, MountedProperty);
        Publish(Ready);
    }

    void Close() noexcept
    {
        watch->stopped = true;
        KillTimer(window, 1);
        SetState(BridgeState::Missing, L"bridge closed");
        for (auto& footer : footers) footer.Remove();
        DestroyWindow(window);
        Publish(Ready);
    }
};
thread_local Context* Context::current;

void RequestRefresh(HWND window) noexcept
{
    auto context = reinterpret_cast<Context*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (context && !context->refreshQueued)
        context->refreshQueued = PostMessageW(window, RefreshMessage, 0, 0) != FALSE;
}

struct Tap : winrt::implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
{
    winrt::com_ptr<IUnknown> site;
    winrt::com_ptr<IXamlDiagnostics> diagnostics;
    std::shared_ptr<Watch> watch;

    HRESULT __stdcall SetSite(IUnknown* value) noexcept override
    {
        if (!value)
        {
            if (watch) watch->stopped = true;
            return S_OK;
        }
        try
        {
            if (watch) return S_OK;
            auto controller = FindWindowW(nullptr, OwnerWindowTitle);
            if (!ControllerActive(controller)) return S_FALSE;
            site.copy_from(value);
            diagnostics = site.as<IXamlDiagnostics>();
            watch = std::make_shared<Watch>();
            watch->controller = controller;
            auto context = std::make_unique<winrt::com_ptr<Tap>>(get_strong());
            if (auto previous = latestWatch.exchange(watch)) previous->stopped = true;
            HANDLE thread = CreateThread(nullptr, 0, [](void* data) -> DWORD
            {
                std::unique_ptr<winrt::com_ptr<Tap>> self(static_cast<winrt::com_ptr<Tap>*>(data));
                HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                auto state = (*self)->watch;
                try
                {
                    // Finish the old subscription before advising on the new
                    // one. The XAML thread never waits for this worker lock.
                    std::lock_guard lock(watchMutex);
                    if (!state->stopped && ControllerActive(state->controller))
                    {
                        auto service = (*self)->site.as<IVisualTreeService>();
                        winrt::check_hresult(service->AdviseVisualTreeChange(self->get()));
                        while (!state->stopped && ControllerActive(state->controller)) Sleep(250);
                        state->stopped = true;
                        service->UnadviseVisualTreeChange(self->get());
                    }
                }
                catch (...) { OutputDebugStringW(L"YogaITS: Quick Settings diagnostics subscription failed.\n"); }
                state->stopped = true;
                latestWatch.compare_exchange_strong(state, nullptr);
                if (SUCCEEDED(initialized)) CoUninitialize();
                return 0;
            }, context.get(), CREATE_SUSPENDED, nullptr);
            if (!thread)
            {
                watch->stopped = true;
                winrt::throw_last_error();
            }
            context.release();
            ResumeThread(thread);
            CloseHandle(thread);
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }

    HRESULT __stdcall GetSite(REFIID iid, void** output) noexcept override
    {
        return site ? site->QueryInterface(iid, output) : E_FAIL;
    }

    HRESULT __stdcall OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override
    {
        if (!watch || watch->stopped || mutation != Add) return S_OK;
        bool isFooter = element.Name && wcscmp(element.Name, L"FooterGrid") == 0;
        bool isPage = element.Type && std::wstring_view(element.Type).ends_with(L"ControlCenterPage");
        if (!isFooter && !isPage) return S_OK;
        try
        {
            wf::IInspectable object{nullptr};
            winrt::check_hresult(diagnostics->GetIInspectableFromHandle(element.Handle,
                reinterpret_cast<::IInspectable**>(winrt::put_abi(object))));
            if (auto target = object.try_as<xaml::FrameworkElement>()) Context::Observe(target, isFooter, watch);
        }
        catch (...) { OutputDebugStringW(L"YogaITS: Quick Settings page or footer observation failed.\n"); }
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
