using YogaITS.Tray.UI.Accessibility;
using YogaITS.Tray.UI.Shell;
using YogaITS.Tray.UI.Controls;
using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Backgrounds;
using YogaITS.Tray.UI.Glass.Capture;
using YogaITS.Tray.UI.Glass.Layout;
using YogaITS.Tray.UI.Glass.Rendering;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Settings;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LiquidGlassWinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI.Glass.Windowing;

// One adapter for real settings windows, information windows and shared tiles.
// The upstream material remains responsible for refraction, blur and highlights.
internal static class LiquidGlassWindow
{
    private static readonly ConditionalWeakTable<UIElement, WindowState> Windows = new();
    private static readonly ConditionalWeakTable<Button, TileAppearance> Tiles = new();
    private static readonly UISettings Palette = new();

    private static readonly GlassPresentationPreference Presentation = new();
    internal static bool NativeRequested => Presentation.NativeRequested;
    internal static event Action? PreferenceChanged;
    internal static event Action? AvailabilityChanged;
    internal static event Action? BackgroundStatusChanged;
    internal static string? ImageError(Window window)=>window.Content is UIElement root && Windows.TryGetValue(root,out var state)
        ? state.ImageError : null;
    internal static void RefreshBackground()=>PreferenceChanged?.Invoke();
    internal static bool Available(Window window) => window.Content is UIElement root &&
        Windows.TryGetValue(root, out var state) && state.Available;

    internal static void SetNativeRequested(bool native)
    {
        if (!Presentation.SetNativeRequested(native)) return;
        PreferenceChanged?.Invoke();
    }

    internal static string? Error(Window window) => window.Content is UIElement root && Windows.TryGetValue(root, out var state)
        ? state.Error : "Missing window material state.";
    internal static bool EffectsEnabled(Window window) => window.Content is UIElement root && Windows.TryGetValue(root, out var state) && state.Effects;
    internal static bool AcceleratesLayout(FrameworkElement element) => Palette.AnimationsEnabled &&
        element.XamlRoot?.Content is UIElement root && Windows.TryGetValue(root, out var state) && state.Effects;
    internal static string Diagnostics(Window window) => window.Content is UIElement root && Windows.TryGetValue(root, out var state)
        ? state.Diagnostics : "Missing window material state.";


    public static void Attach(Window window, Grid root)
    {
        var state = new WindowState(window, root);
        Windows.Add(root, state);
        state.Connect();
    }

    public static void AttachTile(Button button, Grid content, bool selected)
    {
        var surface = new Border
        {
            IsHitTestVisible = false,
            Margin = new Thickness(-button.Padding.Left, -button.Padding.Top, -button.Padding.Right, -button.Padding.Bottom)
        };
        content.Children.Insert(0, surface);
        var material = new Surface(surface, false) { MotionLabel = content.Children.OfType<TextBlock>().FirstOrDefault() };
        // Selection uses the capsule neutral tint plus accent text; hover tint overrides it.
        // No foreground color rectangle; native keyboard focus stays intact.
        var appearance = new TileAppearance(button, material, selected);
        Tiles.Add(button, appearance);
        material.Refreshed = appearance.Refresh;
        button.Loaded += (_, _) =>
        {
            // Core sections may normalize padding after the tile is created.
            surface.Margin = new Thickness(-button.Padding.Left, -button.Padding.Top, -button.Padding.Right, -button.Padding.Bottom);
            if (button.XamlRoot?.Content is UIElement root && Windows.TryGetValue(root, out var state))
                state.Add(material);
            material.Refresh();
        };
        button.IsEnabledChanged += (_, _) => material.SetAvailability(button.IsEnabled);
        material.SetAvailability(button.IsEnabled);
        button.PointerEntered += (_, _) => material.Hover(true);
        button.PointerExited += (_, _) => material.Hover(false);
    }

    private static IEnumerable<UIElement> ChildrenOf(UIElement node)=>node switch
    {
        Panel panel=>panel.Children,
        Border {Child:UIElement child}=>[child],
        ContentControl {Content:UIElement child}=>[child],
        _=>[]
    };
    public static void SetReveal(UIElement element,double progress)
    {
        if(element is Button button && Tiles.TryGetValue(button,out var tile))
        {
            tile.SetReveal(progress); return;
        }
        foreach(var child in ChildrenOf(element)) SetReveal(child,progress);
    }

    public static void SetTileState(Button button, bool emphasized)
    {
        if (!Tiles.TryGetValue(button, out var appearance)) return;
        appearance.Emphasized = emphasized;
        appearance.Refresh();
    }

    public static void RefreshTileAppearance(Button button)
    {
        if (Tiles.TryGetValue(button, out var appearance)) appearance.Refresh();
    }

    private sealed class TileAppearance
    {
        private readonly Button _button;
        private readonly SolidColorBrush _selectedForeground = new();
        private readonly TextBlock? _label;
        private readonly Surface _material;
        private VisualStateGroup? _states;
        private long? _pressSubscription;
        private readonly Thickness _nativeBorder;
        private readonly CornerRadius _nativeCorner;
        private readonly VerticalAlignment _nativeAlignment;
        public bool Emphasized { get; set; }

        public void SetReveal(double progress)
        {
            _material.SetReveal(progress);
            if(_label is not null) _label.Opacity=Math.Clamp(progress,0,1);
        }

        public TileAppearance(Button button, Surface material, bool emphasized)
        {
            _button = button; _material = material; Emphasized = emphasized;
            _label = (button.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
            _nativeBorder = button.BorderThickness;
            _nativeCorner = button.CornerRadius;
            _nativeAlignment = button.VerticalContentAlignment;
            button.Loaded += (_, _) => Connect();
            button.Unloaded += (_, _) => Disconnect();
            button.ActualThemeChanged += (_, _) => Refresh();
            button.SizeChanged += (_, _) => Refresh();
            button.IsEnabledChanged += (_, _) => Refresh();
            Refresh();
        }

        private void Connect()
        {
            Disconnect();
            _button.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(_button) > 0 &&
                VisualTreeHelper.GetChild(_button, 0) is FrameworkElement presenter)
                _states = VisualStateManager.GetVisualStateGroups(presenter).FirstOrDefault(group => group.Name == "CommonStates");
            if (_states is not null) _states.CurrentStateChanged += StateChanged;
            // Observe the native synchronous press state. ButtonBase continues
            // to own mouse/touch capture, Space/Enter and gesture cancellation.
            _pressSubscription = _button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty,
                (_, _) => RefreshPress());
            Refresh();
        }

        private void Disconnect()
        {
            if (_states is not null) _states.CurrentStateChanged -= StateChanged;
            _states = null;
            if (_pressSubscription is long token)
                _button.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, token);
            _pressSubscription = null;
            _material.SetPressed(false);
        }

        private void RefreshPress() => _material.SetPressed(_button.IsEnabled && _button.IsPressed);

        private void StateChanged(object sender, VisualStateChangedEventArgs args) => Refresh();

        public void Refresh()
        {
            bool dark = _material.State?.Dark ?? _button.ActualTheme == ElementTheme.Dark;
            bool glass = _material.State?.Effects == true;
            RefreshPress();
            _button.BorderThickness = glass ? new Thickness(0) : _nativeBorder;
            // The shader owns the pill contour. A rounded XAML presenter
            // would clip the pressure expansion back to the resting rectangle.
            _button.CornerRadius = glass ? new CornerRadius(0) : _nativeCorner;
            _button.VerticalContentAlignment = glass ? VerticalAlignment.Stretch : _nativeAlignment;
            if (!glass)
            {
                // Reuse the original outline brush/state resources. The glass
                // child is hidden; native hover draws in the button template.
                byte channel = dark ? (byte)255 : (byte)0;
                ((SolidColorBrush)_button.Resources["ButtonBackgroundPointerOver"]).Color =
                    UiAccessibility.HighContrast ? UiAccessibility.SystemColor(13) : Color.FromArgb(10, channel, channel, channel);
                ((SolidColorBrush)_button.Resources["ButtonBackgroundPressed"]).Color =
                    UiAccessibility.HighContrast ? UiAccessibility.SystemColor(13) : Color.FromArgb(16, channel, channel, channel);
                _button.ClearValue(Control.ForegroundProperty);
                if (_label is not null) OptionLabelAppearance.Apply(_button, _label);
                return;
            }
            ((SolidColorBrush)_button.Resources["ButtonBackgroundPointerOver"]).Color = Microsoft.UI.Colors.Transparent;
            ((SolidColorBrush)_button.Resources["ButtonBackgroundPressed"]).Color = Microsoft.UI.Colors.Transparent;
            string? input = _button.IsEnabled ? _states?.CurrentState?.Name : null;
            bool hovering = input is "PointerOver" or "Pressed";
            Color color;
            if (UiAccessibility.HighContrast)
                color = Emphasized || hovering ? UiAccessibility.SystemColor(13) : Microsoft.UI.Colors.Transparent;
            else if (hovering)
            {
                // Hover/press highlights stay neutral and override the selected neutral tint.
                byte channel = dark ? (byte)255 : (byte)0;
                color = Color.FromArgb(input == "Pressed" ? (byte)42 : (byte)26, channel, channel, channel);
            }
            else
            {
                byte channel = dark ? (byte)255 : (byte)0;
                color = Emphasized
                    ? Color.FromArgb(GlassMaterialPolicy.SelectionTintAlpha, channel, channel, channel)
                    : Microsoft.UI.Colors.Transparent;
            }
            _material.SetStateTint(color);
            if (UiAccessibility.HighContrast && (Emphasized || hovering))
                _button.Foreground = new SolidColorBrush(UiAccessibility.SystemColor(14));
            else if (Emphasized)
            {
                _selectedForeground.Color = Palette.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
                _button.Foreground = _selectedForeground;
            }
            else _button.ClearValue(Control.ForegroundProperty);
            // The native Button hover state sets ContentPresenter.Foreground.
            // A local label value keeps selection visible in every pointer state.
            if (_label is not null)
                OptionLabelAppearance.Apply(_button, _label, Emphasized ? _button.Foreground : null);
        }

    }

    private static int SetWindowFrame(Window window, bool glass)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        uint corners = glass ? 2u : 0u; // DWMWCP_ROUND for glass; native DEFAULT otherwise.
        _ = DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(uint));
        uint color = glass ? 0xFFFFFFFEu : 0xFFFFFFFFu; // NONE for glass, native DEFAULT otherwise.
        return DwmSetWindowAttribute(hwnd, 34, ref color, sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);


    private sealed class WindowState(Window window, Grid root)
    {
        private readonly UISettings _settings = new();
        private readonly AccessibilitySettings _contrast = new();
        private readonly List<WeakReference<Surface>> _surfaces = [];
        private Surface? _background;
        private DesktopCaptureBackdrop? _capture;
        private XamlRoot? _xamlRoot;
        private double _scale = double.NaN;
        private bool _closed, _failed, _contrastSubscribed, _refreshing;
        private string? _failure;
        private bool _active = true;

        public Grid Root => root;
        public string? Error => _failure ?? LiquidGlassBrush.LastError ?? _capture?.Error;
        public int BorderResult { get; private set; }
        private bool? _lastAvailable;
        public bool Available => !_closed && !_failed && Error is null &&
            GlassCapability.Supported && !_contrast.HighContrast && _settings.AdvancedEffectsEnabled;
        public bool Effects => Presentation.UseGlass(Available);
        public bool Active => _active;
        public string? ImageError => _capture?.ImageError;
        public bool Dark => root.ActualTheme == ElementTheme.Dark;
        public double WindowRadius => window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized } ? 0 : GlassEdgePolicy.WindowRadiusDip;
        public BackgroundTuning Tuning => BackgroundTuning.Default;
        public Microsoft.UI.Composition.CompositionBrush? SamplingBrush => _capture?.SamplingBrush;
        public string Diagnostics => $"主题 {(Dark ? "暗" : "亮")} / 材质 {(Effects ? "液态玻璃" : "原版 WinUI 3 / Mica")} / 激活 {_active}\n身份：{YogaITS.Tray.UI.Glass.Runtime.CaptureIdentityBootstrap.Status}\n{_capture?.Diagnostics}\n{Error}";


        public void Connect()
        {
            var layer = new Border
            {
                IsHitTestVisible = false,
                Margin = new Thickness(-root.Padding.Left, -root.Padding.Top, -root.Padding.Right, -root.Padding.Bottom)
            };
            Grid.SetRowSpan(layer, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(layer, Math.Max(1, root.ColumnDefinitions.Count));
            root.Children.Insert(0, layer);
            _background = new Surface(layer, true);
            Add(_background);
            root.Loaded += Loaded;
            root.ActualThemeChanged += ThemeChanged;
            window.Activated += Activated;
            window.Closed += Closed;
            _settings.AdvancedEffectsEnabledChanged += EffectsChanged;
            _settings.ColorValuesChanged += EffectsChanged;
            LiquidGlassBrush.PipelineFailed += PipelineFailed;
            PreferenceChanged += Refresh;
            // Some unpackaged SDK/OS combinations expose the getter but reject
            // this WinRT event. Color/activation notifications remain available.
            try { _contrast.HighContrastChanged += ContrastChanged; _contrastSubscribed = true; }
            catch (System.Runtime.InteropServices.COMException ex)
            { System.Diagnostics.Debug.WriteLine("Contrast event unavailable: " + ex.HResult); }
            CompositionTarget.Rendering += VerifyRender;
            Refresh();
        }

        public void Add(Surface surface)
        {
            if (surface.State == this) return;
            surface.State = this;
            _surfaces.Add(new(surface));
        }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (_xamlRoot is not null) _xamlRoot.Changed -= RootChanged;
            _xamlRoot = root.XamlRoot;
            if (_xamlRoot is not null) _xamlRoot.Changed += RootChanged;
            Refresh();
        }

        private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            if (_scale != sender.RasterizationScale) Refresh();
        }
        private void ThemeChanged(FrameworkElement sender, object args) => Refresh();
        private void EffectsChanged(UISettings sender, object args) => QueueRefresh();
        private void ContrastChanged(AccessibilitySettings sender, object args) => QueueRefresh();
        private void PipelineFailed(string error)
        {
            if (_closed || _failed) return;
            _failed = true; _failure = error;
            CaptureDiagnostics.Write("Glass pipeline: " + error);
            QueueRefresh();
        }
        public void MaterialFailed(Exception error) => PipelineFailed(error.ToString());
        private void CaptureFailed() => QueueRefresh();
        private void BackgroundContentChanged()=>BackgroundStatusChanged?.Invoke();
        private void QueueRefresh() => root.DispatcherQueue.TryEnqueue(() => { if (!_closed) Refresh(); });
        private void Activated(object sender, WindowActivatedEventArgs args)
        {
            _active = args.WindowActivationState != WindowActivationState.Deactivated;
            CaptureWindowExclusion.Invalidate();
            Refresh();
        }

        private void VerifyRender(object? sender, object args)
        {
            CompositionTarget.Rendering -= VerifyRender;
            if (_closed || string.IsNullOrEmpty(LiquidGlassBrush.LastError)) return;
            _failed = true;
            System.Diagnostics.Debug.WriteLine("Liquid glass fallback: " + LiquidGlassBrush.LastError);
            Refresh();
        }

        private void Refresh()
        {
            if (_closed || _refreshing) return;
            _refreshing = true;
            try
            {
                _scale = root.XamlRoot?.RasterizationScale ?? 1;
                bool effects = Effects;
                if (effects && _capture is null)
                {
                    _capture = new DesktopCaptureBackdrop(window, root);
                    _capture.Failed += CaptureFailed;
                    _capture.ContentChanged += BackgroundContentChanged;
                    _capture.Connect();
                }
                _capture?.Refresh(effects, effects, Tuning.BlurDip);
                effects = Effects; // A synchronous capture failure uses native UI immediately.
                if (_capture is not null)
                {
                    if (!effects) _capture.Refresh(false, false, Tuning.BlurDip);
                    _capture.View.Visibility = effects ? Visibility.Visible : Visibility.Collapsed;
                }
                if (effects)
                {
                    window.SystemBackdrop = null;
                    root.ClearValue(Panel.BackgroundProperty);
                }
                else
                {
                    // Exactly the normal app's backdrop; WinUI owns activation,
                    // theme, transparency preferences and its system solid fallback.
                    try
                    {
                        if (window.SystemBackdrop is not MicaBackdrop) window.SystemBackdrop = new MicaBackdrop();
                        root.ClearValue(Panel.BackgroundProperty);
                    }
                    catch (Exception ex)
                    {
                        CaptureDiagnostics.Write("Native Mica unavailable: " + ex);
                        window.SystemBackdrop = null;
                        root.Background = new SolidColorBrush(UiAccessibility.HighContrast ? UiAccessibility.SystemColor(5) :
                            Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
                    }
                }
                for (int i = _surfaces.Count - 1; i >= 0; i--)
                {
                    if (_surfaces[i].TryGetTarget(out var surface)) surface.Refresh();
                    else _surfaces.RemoveAt(i);
                }
                // Initial style restoration can precede Loaded for dynamic feature groups.
                // Refresh every presentation after the actual material state is established.
                OptionGroupPresentation.RefreshTree(root);
                AccelerationLayoutMotion.RefreshTree(root);
                BorderResult = SetWindowFrame(window, effects);
                if (window is TrayMenuWindow menu) menu.RefreshMaterialAppearance();
                if (effects) _capture?.RefreshGeometry();
            }
            catch (Exception ex)
            {
                bool firstFailure = !_failed;
                _failed = true; _failure = ex.ToString();
                CaptureDiagnostics.Write("Glass presentation fallback: " + ex);
                if (_capture is not null)
                {
                    _capture.View.Visibility = Visibility.Collapsed;
                    try { _capture.Refresh(false, false, Tuning.BlurDip); }
                    catch (Exception stopError) { CaptureDiagnostics.Write("Capture shutdown: " + stopError); }
                }
                // One retry installs the native presentation. A recurring native
                // failure must not create an endless dispatcher/error loop.
                if (firstFailure) QueueRefresh();
            }
            finally
            {
                _refreshing = false;
                bool available = Available;
                if (_lastAvailable != available)
                {
                    _lastAvailable = available;
                    if (window is TrayMenuWindow menu) menu.RefreshUiOptions();
                    AvailabilityChanged?.Invoke();
                }
            }
        }

        private void Closed(object sender, WindowEventArgs args)
        {
            _closed = true;
            CompositionTarget.Rendering -= VerifyRender;
            _settings.AdvancedEffectsEnabledChanged -= EffectsChanged;
            _settings.ColorValuesChanged -= EffectsChanged;
            LiquidGlassBrush.PipelineFailed -= PipelineFailed;
            PreferenceChanged -= Refresh;
            if (_contrastSubscribed) _contrast.HighContrastChanged -= ContrastChanged;
            root.Loaded -= Loaded;
            root.ActualThemeChanged -= ThemeChanged;
            if (_xamlRoot is not null) _xamlRoot.Changed -= RootChanged;
            window.Activated -= Activated;
            window.Closed -= Closed;
            window.SystemBackdrop = null;
            foreach (var weak in _surfaces)
                if (weak.TryGetTarget(out var surface)) surface.ReleaseButtonMotion();
            if (_capture is not null) { _capture.Failed -= CaptureFailed; _capture.ContentChanged-=BackgroundContentChanged; _capture.Dispose(); }
            Windows.Remove(root);
            _surfaces.Clear();
        }
    }

    private sealed class Surface
    {
        private readonly Border _view;
        private readonly bool _windowBackground;
        private LiquidGlassBrush? _glass;
        private GlassTabSurface? _buttonSurface;
        private AccelerationTabMotion? _buttonMotion;
        private GlassMotionLayer? _buttonLayer;
        private bool _pressed;
        private TabFrame _buttonTarget;
        private double _reveal=1, _availability=1;
        private RenderOffset? _labelOffset;
        public FrameworkElement? MotionLabel { get; init; }
        private bool BrushAttached => _windowBackground ? _view.Background == _glass :
            _buttonSurface?.Brush == _glass && _buttonSurface is not null &&
            (_buttonLayer?.IsRaised == true || ElementCompositionPreview.GetElementChildVisual(_view) == _buttonSurface.Visual);
        public WindowState? State { get; set; }
        public Action? Refreshed { get; set; }
        public Color StateTint { get; private set; }
        public double Opacity => _view.Opacity;
        public bool Visible => _view.Visibility == Visibility.Visible;

        public Surface(Border view, bool windowBackground)
        {
            _view = view; _windowBackground = windowBackground;
            view.SizeChanged += (_, _) => Refresh();
            view.Unloaded += (_, _) => ReleaseButtonMotion();
        }

        public void Refresh()
        {
            try { RefreshCore(); }
            catch (Exception ex)
            {
                ReleaseButtonMotion();
                _view.Background = null;
                _view.Visibility = Visibility.Collapsed;
                State?.MaterialFailed(ex);
            }
        }

        private void RefreshCore()
        {
            Refreshed?.Invoke();
            if (State?.Effects != true)
            {
                ReleaseButtonMotion();
                _view.Background = null;
                _view.Visibility = Visibility.Collapsed;
                return;
            }
            _view.Visibility = Visibility.Visible;
            double scale = _view.XamlRoot?.RasterizationScale ?? 1;
            if (!_windowBackground)
            {
                RefreshButtonSurface((float)scale);
                return;
            }
            _glass ??= new LiquidGlassBrush { ShapeRoundness = 2, ShapeInset = GlassEdgePolicy.ShapeInset };
            _glass.Dpr = (float)scale;
            // The client fills the complete HWND in glass mode; DWM owns the final
            // rounded clip. Do not add a tint apron outside the optical highlight.
            _glass.FillWindowBackground = 0;
            _glass.BlurAmount = GlassScalePolicy.BlurAmount(scale, true);
            _glass.BackgroundBlurAmount = 0; // Real Gaussian already ran on the full captured source.
            _glass.BackgroundSource = null; // Sample the visible captured in-app layer through the proven backdrop path.
            _glass.ShapeInset = GlassEdgePolicy.ShapeInset;
            _glass.RefFresnelRange = _glass.GlareRange = GlassScalePolicy.HighlightRange(scale);
            double shorterSide = Math.Min(_view.ActualWidth, _view.ActualHeight);
            double radius = State!.WindowRadius;
            // The shader owns the window's rounded shape. A second rounded
            // Border mask attenuates the same edge again and exposes untinted input.
            _view.CornerRadius = new CornerRadius(0);
            _glass.ShapeRadius = GlassEdgePolicy.ShapeRadius(shorterSide, radius);
            UpdateTint();
            _view.Background = _glass;
        }

        private void RefreshButtonSurface(float scale)
        {
            float width = (float)_view.ActualWidth, height = (float)_view.ActualHeight;
            if (width <= 0 || height <= 0) return;
            var rest = new TabFrame(0, 0, width, height);
            if (_buttonSurface is null)
            {
                var compositor = ElementCompositionPreview.GetElementVisual(_view).Compositor;
                _buttonSurface = new GlassTabSurface(compositor);
                _buttonMotion = new AccelerationTabMotion(PublishButtonFrame,
                    () => State?.Effects == true && Palette.AnimationsEnabled, layout: true, elasticLayout: true);
                _buttonTarget = rest;
                if (MotionLabel is not null) _labelOffset = new RenderOffset(MotionLabel);
                _buttonSurface.SetFrame(rest, false);
                _buttonMotion.SetTarget(rest, false);
                ElementCompositionPreview.SetElementChildVisual(_view, _buttonSurface.Visual);
                _buttonLayer = new GlassMotionLayer(_view, _buttonSurface, State!.Root);
                _buttonMotion.LayerRequested += _buttonLayer.SetRaised;
            }
            else if (_buttonTarget != rest)
            {
                _buttonTarget = rest;
                _buttonMotion!.SetTarget(rest, Palette.AnimationsEnabled);
            }
            // Identical material and four-edge pressure to a stationary tab
            // capsule, without a second rounded XAML mask or layout animation.
            _view.CornerRadius = new CornerRadius(0);
            _view.Background = null;
            _buttonSurface.SetMaterial(true, Microsoft.UI.Colors.Transparent, Microsoft.UI.Colors.Transparent,
                scale, shadow: true, updateTint: false);
            _glass = _buttonSurface.Brush;
            UpdateTint();
            _buttonMotion!.SetPressed(_pressed, Palette.AnimationsEnabled);
        }

        private void PublishButtonFrame(TabFrame frame,System.Numerics.Vector2 target)
        {
            float reveal=(float)_reveal;
            var shown=frame with {Y=frame.Y*reveal,Height=Math.Max(1,frame.Height*reveal)};
            _buttonSurface?.SetPhysicsFrame(shown,new(target.X,Math.Max(1,target.Y*reveal)));
            _labelOffset?.Set(new(shown.Center-target.X/2,shown.CenterY-target.Y/2));
        }
        public void SetAvailability(bool enabled)
        {
            _availability=enabled?1:.4;
            _view.Opacity=_availability*_reveal;
        }
        public void SetReveal(double progress)
        {
            _reveal=Math.Clamp(progress,0,1);
            _view.Opacity=_availability*_reveal;
            if(_buttonMotion is not null)
            {
                var target=_buttonMotion.Physics.Target;
                PublishButtonFrame(_buttonMotion.Physics.Frame,new(target.Width,target.Height));
            }
        }

        public void SetPressed(bool pressed)
        {
            _pressed = pressed;
            _buttonMotion?.SetPressed(pressed, State?.Effects == true && Palette.AnimationsEnabled);
        }

        public void ReleaseButtonMotion()
        {
            if (_buttonSurface is null) return;
            _buttonMotion?.Dispose();
            _buttonMotion = null;
            _labelOffset?.Dispose();
            _labelOffset = null;
            _buttonLayer?.Dispose();
            _buttonLayer = null;
            ElementCompositionPreview.SetElementChildVisual(_view, null);
            _buttonSurface.Dispose();
            _buttonSurface = null;
            _glass = null;
        }

        public void SetStateTint(Color color)
        {
            if (StateTint == color) return;
            StateTint = color;
            if (State?.Effects == true && _glass is not null) UpdateTint();
            else if (!_windowBackground) _view.Background = null;
        }

        private void UpdateTint()
        {
            if (_glass is null) return;
            bool dark = State?.Dark ?? _view.ActualTheme == ElementTheme.Dark;
            GlassTint tint = GlassMaterialPolicy.Tint(dark, _windowBackground,
                new(StateTint.R, StateTint.G, StateTint.B, StateTint.A / 255.0), State?.Tuning.TintAmount(dark));
            _glass.SetTint(tint.Red, tint.Green, tint.Blue, tint.Amount,
                Palette.AnimationsEnabled ? 100 : 0);
        }


        public void Hover(bool entered)
        {
            if (_glass is null || State?.Effects != true) return;
            double duration = new UISettings().AnimationsEnabled ? 100 : 0;
            if (duration > 0) _glass.AnimateScalar("GlareFactor", entered ? 100 : 90, duration);
            else _glass.GlareFactor = entered ? 100 : 90;
        }
    }
}
