using System.Numerics;
using LiquidGlassWinUI;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using YogaITS.Tray.UI;

namespace YogaITS.Tray.Demos.FluidTabs;

internal sealed class FluidTabBar : Grid, IDisposable
{
    public static readonly string[] Labels = ["智能模式", "安静模式", "性能模式", "创作模式"];
    private readonly Window _window;
    private readonly Canvas _host = new() { IsHitTestVisible = false };
    private readonly RadioButton[] _tabs = new RadioButton[FluidTabGeometry.Count];
    private readonly UISettings _palette = new();
    private ContainerVisual? _visuals;
    private SpriteVisual? _track;
    private RectangleClip? _trackClip;
    private FluidTabIndicator? _indicator;
    private CompositionColorBrush? _trackColor, _selectedColor;
    private LiquidGlassBrush? _trackGlass, _selectedGlass;
    private GlassMotionFeedback? _trackMotion, _selectedMotion;
    private XamlRoot? _root;
    private FluidTabGeometry _geometry;
    private Microsoft.UI.Xaml.Input.Pointer? _pointer;
    private readonly Border _input = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly FluidTabGesture _gesture = new();
    private int _hoverIndex = -1;
    private bool _syncing, _disposed;
    public int SelectedIndex { get; private set; }
    public event Action<int>? SelectionChanged;

    public FluidTabBar(Window window)
    {
        _window = window;
        Height = 64; MinWidth = 352;
        Background = new SolidColorBrush(Colors.Transparent);
        Children.Add(_host);
        var labels = CreateLabelLayer(_tabs);
        for (int i = 0; i < _tabs.Length; i++)
        {
            int index = i;
            _tabs[i].Checked += (_, _) => { if (!_syncing) Select(index, true); };
        }
        Children.Add(labels);
        // A dedicated pointer surface avoids RadioButton's internal capture
        // taking the gesture before an ancestor can receive it.
        Children.Add(_input);
        SyncSelection();
        _input.PointerPressed += Pressed;
        _input.PointerMoved += Moved;
        _input.PointerReleased += Released;
        _input.PointerCanceled += Canceled; _input.PointerCaptureLost += Canceled;
        _input.PointerExited += (_, _) => { _hoverIndex = -1; RefreshMaterial(); };
        PreviewKeyDown += KeyPressed;
        Loaded += Connect; Unloaded += Disconnect;
        SizeChanged += (_, _) => Resize();
        ActualThemeChanged += (_, _) => RefreshMaterial();
    }

    internal static Grid CreateLabelLayer(RadioButton[] tabs)
    {
        var labels = new Grid { Margin = new Thickness(FluidTabGeometry.Padding, 0, FluidTabGeometry.Padding, 0) };
        var style = CreateTabStyle();
        string group = "FluidPerformance" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < tabs.Length; i++)
        {
            labels.ColumnDefinitions.Add(new ColumnDefinition());
            var tab = new RadioButton
            {
                Content = Labels[i], GroupName = group, Style = style,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0), Padding = new Thickness(0)
            };
            AutomationProperties.SetName(tab, "性能选项中的" + Labels[i]);
            Grid.SetColumn(tab, i); labels.Children.Add(tab); tabs[i] = tab;
        }
        return labels;
    }

    internal static Style CreateTabStyle() => (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
                <Setter Property="HorizontalContentAlignment" Value="Center" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
                <Setter Property="UseSystemFocusVisuals" Value="True" />
                <Setter Property="Template"><Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Grid Background="{TemplateBinding Background}" MinHeight="44">
                            <ContentPresenter Content="{TemplateBinding Content}"
                                HorizontalAlignment="Center" VerticalAlignment="Center"
                                Foreground="{TemplateBinding Foreground}" FontWeight="{TemplateBinding FontWeight}" FontSize="16" />
                        </Grid>
                    </ControlTemplate>
                </Setter.Value></Setter>
            </Style>
            """);

    private void Connect(object sender, RoutedEventArgs args)
    {
        if (_disposed || _visuals is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _visuals = compositor.CreateContainerVisual();
        _track = compositor.CreateSpriteVisual();
        _trackClip = compositor.CreateRectangleClip();
        _indicator = new(compositor);
        _trackMotion = new GlassMotionFeedback(_track, track: true);
        _selectedMotion = new GlassMotionFeedback(_indicator.Visual);
        _indicator.MovingChanged += MotionChanged;
        _trackColor = compositor.CreateColorBrush(); _selectedColor = compositor.CreateColorBrush();
        _visuals.Children.InsertAtBottom(_track);
        _visuals.Children.InsertAtTop(_indicator.Visual);
        ElementCompositionPreview.SetElementChildVisual(_host, _visuals);
        _root = XamlRoot;
        if (_root is not null) _root.Changed += RootChanged;
        LiquidGlassWindow.PreferenceChanged += RefreshMaterial;
        LiquidGlassWindow.AvailabilityChanged += RefreshMaterial;
        _palette.ColorValuesChanged += PaletteChanged;
        Resize(); RefreshMaterial();
    }

    private void MotionChanged(bool moving) => RefreshMaterial();
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshMaterial();
    private void PaletteChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshMaterial);
    private void Resize()
    {
        _geometry = FluidTabGeometry.Create(ActualWidth, ActualHeight);
        if (_visuals is null || _track is null) return;
        _visuals.Size = _track.Size = new Vector2(_geometry.Width, _geometry.Height);
        if (_trackClip is not null)
        {
            _trackClip.Right = _geometry.Width; _trackClip.Bottom = _geometry.Height;
            var radius = new Vector2(Math.Min(_geometry.Width, _geometry.Height) / 2);
            _trackClip.TopLeftRadius = _trackClip.TopRightRadius = _trackClip.BottomLeftRadius = _trackClip.BottomRightRadius = radius;
        }
        _indicator?.Configure(_geometry, SelectedIndex);
        _trackMotion?.SetRestWidth(_geometry.Width);
        _selectedMotion?.SetRestWidth(_geometry.Cell);
    }

    private void RefreshMaterial()
    {
        if (_visuals is null || _track is null || _indicator is null || _trackColor is null || _selectedColor is null) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        var accent = UiAccessibility.HighContrast ? UiAccessibility.SystemColor(13) : _palette.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        bool hovered = _hoverIndex == SelectedIndex;
        byte channel = dark ? (byte)255 : (byte)0;
        var selected = Color.FromArgb(0, channel, channel, channel);
        for (int i = 0; i < _tabs.Length; i++)
        {
            if (i == SelectedIndex) _tabs[i].Foreground = new SolidColorBrush(UiAccessibility.HighContrast ? UiAccessibility.SystemColor(14) : accent);
            else _tabs[i].ClearValue(Control.ForegroundProperty);
        }
        _trackColor.Color = UiAccessibility.HighContrast ? UiAccessibility.SystemColor(5) : Color.FromArgb(22, channel, channel, channel);
        _selectedColor.Color = UiAccessibility.HighContrast ? UiAccessibility.SystemColor(13) : Color.FromArgb(26, channel, channel, channel);
        bool glass = LiquidGlassWindow.EffectsEnabled(_window);
        _track.Clip = glass ? null : _trackClip;
        _indicator.NativeFallback = !glass;
        bool motion = _indicator.IsMoving || _gesture.IsDragging;
        _trackMotion?.SetActive(motion, glass && _palette.AnimationsEnabled);
        _selectedMotion?.SetActive(motion, glass && _palette.AnimationsEnabled);
        if (!glass)
        {
            _track.Brush = _trackColor; _indicator.Visual.Brush = _selectedColor;
            DisconnectGlass();
            return;
        }
        try
        {
            if (_trackGlass is null)
            {
                _trackGlass = NewGlass(); _selectedGlass = NewGlass();
                _track.Brush = _trackGlass.ConnectToVisual();
                _indicator.Visual.Brush = _selectedGlass.ConnectToVisual();
            }
            float scale = (float)(XamlRoot?.RasterizationScale ?? 1);
            foreach (var material in new[] { _trackGlass, _selectedGlass! })
            {
                material.Dpr = scale;
                material.RefFresnelRange = material.GlareRange = GlassScalePolicy.HighlightRange(scale);
            }
            var trackTint = GlassMaterialPolicy.Tint(dark, false, default);
            _trackGlass.SetTint(trackTint.Red, trackTint.Green, trackTint.Blue, trackTint.Amount);
            _selectedGlass!.SetTint(selected.R, selected.G, selected.B, 0);
            _trackMotion?.BindGlass(_trackGlass);
            _selectedMotion?.BindGlass(_selectedGlass);
            _selectedGlass.GlareFactor = hovered ? 100 : 90;
        }
        catch (Exception ex)
        {
            _track.Brush = _trackColor; _indicator.Visual.Brush = _selectedColor;
            _track.Clip = _trackClip; _indicator.NativeFallback = true;
            DisconnectGlass();
            CaptureDiagnostics.Write("Fluid tab material fallback: " + ex);
        }
    }

    private static LiquidGlassBrush NewGlass() => new()
    {
        ShapeRoundness = 2, ShapeRadius = 1, ShapeInset = 0,
        BlurAmount = 0, BackgroundBlurAmount = 0, FillWindowBackground = 0
    };
    private void DisconnectGlass()
    {
        _trackMotion?.BindGlass(null); _selectedMotion?.BindGlass(null);
        _trackGlass?.DisconnectFromVisual(); _selectedGlass?.DisconnectFromVisual();
        _trackGlass = _selectedGlass = null;
    }

    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                _tabs[i].IsChecked = i == SelectedIndex;
                _tabs[i].IsTabStop = i == SelectedIndex;
                _tabs[i].FontWeight = i == SelectedIndex ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        finally { _syncing = false; }
    }
    internal void SelectIndex(int index, bool animate = false) => Select(index, animate);
    private void Select(int index, bool animate, bool followPointer = false)
    {
        index = Math.Clamp(index, 0, _tabs.Length-1);
        bool changed = SelectedIndex != index;
        SelectedIndex = index; SyncSelection();
        if (!followPointer) _indicator?.Select(index, animate && _palette.AnimationsEnabled);
        RefreshMaterial();
        if (changed) SelectionChanged?.Invoke(index);
    }

    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (_pointer is not null || (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)) return;
        if (!_input.CapturePointer(args.Pointer))
        {
            CaptureDiagnostics.Write("Fluid pointer capture failed: " + args.Pointer.PointerDeviceType);
            return;
        }
        if (!_gesture.Begin(args.Pointer.PointerId, point.Position.X, SelectedIndex, _geometry))
        {
            _input.ReleasePointerCapture(args.Pointer); return;
        }
        _pointer = args.Pointer;
        CaptureDiagnostics.Write($"Fluid pointer captured: type={args.Pointer.PointerDeviceType}, x={point.Position.X:F1} DIP.");
        int index = _geometry.IndexAt(point.Position.X);
        _hoverIndex = index;
        Select(index, true); _tabs[index].Focus(FocusState.Pointer);
        args.Handled = true;
    }
    private void FollowPointer(PointerRoutedEventArgs args)
    {
        double x = args.GetCurrentPoint(this).Position.X;
        if (!_gesture.Move(args.Pointer.PointerId, x, _geometry, out float left, out int index)) return;
        Select(index, true, true);
        _indicator?.DragTo(left, _palette.AnimationsEnabled);
    }
    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        int hover = _geometry.IndexAt(args.GetCurrentPoint(this).Position.X);
        if (_hoverIndex != hover) { _hoverIndex = hover; RefreshMaterial(); }
        if (!_gesture.Owns(args.Pointer.PointerId)) return;
        FollowPointer(args);
        args.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs args)
    {
        if (!_gesture.Owns(args.Pointer.PointerId)) return;
        FollowPointer(args);
        bool dragging = _gesture.IsDragging;
        CaptureDiagnostics.Write($"Fluid pointer released: dragged={dragging}, selected={SelectedIndex}.");
        var pointer = _pointer; _pointer = null; _gesture.End();
        if (dragging) _indicator?.Select(SelectedIndex, _palette.AnimationsEnabled);
        RefreshMaterial();
        if (pointer is not null) _input.ReleasePointerCapture(pointer);
        _tabs[SelectedIndex].Focus(FocusState.Pointer);
        args.Handled = true;
    }
    private void Canceled(object sender, PointerRoutedEventArgs args)
    {
        if (!_gesture.Owns(args.Pointer.PointerId)) return;
        int original = _gesture.OriginalIndex;
        _pointer = null; _gesture.End(); Select(original, true);
    }
    private void KeyPressed(object sender, KeyRoutedEventArgs args)
    {
        int target;
        switch (args.Key)
        {
            case VirtualKey.Left: target = FluidTabGeometry.Next(SelectedIndex, -1); break;
            case VirtualKey.Right: target = FluidTabGeometry.Next(SelectedIndex, 1); break;
            case VirtualKey.Home: target = 0; break;
            case VirtualKey.End: target = _tabs.Length-1; break;
            case VirtualKey.Escape when _pointer is not null:
                int original = _gesture.OriginalIndex;
                _pointer = null; _gesture.End(); _input.ReleasePointerCaptures();
                Select(original, true); _tabs[original].Focus(FocusState.Keyboard); args.Handled = true; return;
            default: return;
        }
        Select(target, true); _tabs[target].Focus(FocusState.Keyboard); args.Handled = true;
    }

    private void Disconnect(object sender, RoutedEventArgs args)
    {
        _pointer = null; _gesture.End(); _input.ReleasePointerCaptures();
        LiquidGlassWindow.PreferenceChanged -= RefreshMaterial;
        LiquidGlassWindow.AvailabilityChanged -= RefreshMaterial;
        _palette.ColorValuesChanged -= PaletteChanged;
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _visuals?.Children.RemoveAll();
        if (_indicator is not null) _indicator.MovingChanged -= MotionChanged;
        _trackMotion?.Dispose(); _selectedMotion?.Dispose();
        _trackMotion = _selectedMotion = null;
        _indicator?.Dispose(); _track?.Dispose(); _trackClip?.Dispose(); _trackClip = null; _visuals?.Dispose();
        DisconnectGlass(); _trackColor?.Dispose(); _selectedColor?.Dispose();
        _indicator = null; _track = null; _visuals = null; _trackColor = _selectedColor = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Disconnect(this, new RoutedEventArgs());
    }
}
