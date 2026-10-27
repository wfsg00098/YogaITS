using YogaITS.Tray.UI.Accessibility;
using YogaITS.Tray.UI.Controls;
using YogaITS.Tray.UI.Shell;
using YogaITS.Tray.UI.Glass.Animation;
using YogaITS.Tray.UI.Glass.Layout;
using YogaITS.Tray.UI.Glass.Rendering;
using YogaITS.Tray.UI.Glass.Runtime;
using YogaITS.Tray.UI.Glass.Windowing;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace YogaITS.Tray.UI.Glass.Controls;

internal sealed class GlassOptionBar : Grid, IDisposable
{
    private readonly Window _window;
    private readonly bool _useAcceleration;
    private int _columns;
    private string[] _optionLabels;
    private readonly string _groupName;
    private readonly double _fontSize;
    private readonly bool _compactLabels;
    private readonly bool _fitToContent;
    private double[]? _naturalWidths;
    private readonly string _radioGroup;
    private readonly AccelerationLayoutMotion _labelLayout;
    private sealed record LabelCell(RadioButton Tab, TextBlock Caption, SolidColorBrush Foreground, bool Enabled);
    private readonly Dictionary<string, LabelCell> _retiring = new(StringComparer.Ordinal);
    private readonly Dictionary<RadioButton, TabLabelFade> _fades = [];
    private float _reveal = 1;
    private AccelerationTabMotion? _trackAcceleration;
    private AccelerationTabMotion? _acceleration;
    private GlassMotionLayer? _motionLayer;
    private readonly Canvas _back = new() { IsHitTestVisible = false };
    private readonly Canvas _labels;
    private readonly Canvas _capsuleLayer = new() { IsHitTestVisible = false };
    private readonly Border _input = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private RadioButton[] _tabs;
    private TextBlock[] _captions;
    private SolidColorBrush[] _foregrounds;
    private OptionSelectionState _selection;
    private readonly UISettings _palette = new();
    private readonly SelectionMotionState _state = new();
    private readonly FloatingTabGesture _gesture = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _dragIdle;
    private ContainerVisual? _scene;
    private GlassTabSurface? _track, _active;
    private Compositor? _compositor;
    private CompositionScopedBatch? _batch;
    private ChoiceTrackLayout _layout;
    private XamlRoot? _root;
    private Pointer? _pointer;
    private int _original;
    private bool _syncing, _disposed, _hasPresentedLayout;
    public int SelectedIndex => _selection.SelectedIndex;
    public int OptionCount => _selection.Count;
    public bool IsPending => _selection.IsBusy;
    public event Action<int>? SelectionRequested;
    public event Action<int>? SelectionChanged;

    public GlassOptionBar(Window window, bool acceleration = false,
        IReadOnlyList<string>? options = null, string groupName = "性能选项", double fontSize = TrayMenuWindow.ControlCenterButtonFontSize, bool compactLabels = false, int columns = 0, bool fitToContent = false)
    {
        var labels = options?.ToArray() ?? new[] { "智能模式", "安静模式", "性能模式", "创作模式" }.ToArray();
        _optionLabels = labels;
        _groupName = groupName;
        _fontSize = fontSize;
        _compactLabels = compactLabels;
        _fitToContent = fitToContent;
        _selection = new(labels.Length);
        _tabs = new RadioButton[labels.Length];
        _captions = new TextBlock[labels.Length];
        _foregrounds = new SolidColorBrush[labels.Length];
        _columns = columns == 0 ? labels.Length : columns;
        if (_columns < 1 || _columns > labels.Length) throw new ArgumentOutOfRangeException(nameof(columns));
        _layout = ChoiceTrackLayout.Create(600, ChoiceTrackLayout.NaturalHeight(labels.Length, _columns), labels.Length, _columns);
        _window = window;
        _useAcceleration = acceleration;
        // One-shot stop detection, never an animation/frame clock.
        _dragIdle = DispatcherQueue.CreateTimer();
        _dragIdle.Interval = TimeSpan.FromMilliseconds(45);
        _dragIdle.IsRepeating = false;
        _dragIdle.Tick += DragIdleTick;
        Height = ChoiceTrackLayout.NaturalHeight(labels.Length, _columns);
        if (_layout.Rows > 1) _input.ManipulationMode = ManipulationModes.None;
        MinWidth = 0;
        // A tile row has its grid margin plus the outer tile's host margin.
        // Align the track contour with the card contour in either window.
        Margin = new Thickness(TrayMenuWindow.OptionTrackHorizontalMarginDip, 0,
            TrayMenuWindow.OptionTrackHorizontalMarginDip, 0);
        _labels = CreateLabelLayer(_tabs, _captions, _foregrounds, labels, groupName, fontSize, compactLabels);
        _radioGroup = _tabs[0].GroupName;
        _labelLayout = AccelerationLayoutMotion.Attach(_labels, centers: true);
        // Backdrop sampling sees the actual text below the refracting capsule.
        // The input layer remains above everything but paints no visible pixels.
        Children.Add(_back);
        Children.Add(_labels);
        Children.Add(_capsuleLayer);
        Children.Add(_input);
        foreach (var tab in _tabs) WireTab(tab);
        _input.PointerPressed += Pressed;
        _input.PointerMoved += Moved;
        _input.PointerReleased += Released;
        _input.PointerCanceled += Canceled;
        _input.PointerCaptureLost += Canceled;
        PreviewKeyDown += KeyPressed;
        Loaded += Connect;
        Unloaded += Disconnect;
        SizeChanged += (_, _) =>
        {
            // A resizing host invalidates the gesture's geometry snapshot.
            CancelPointer();
            ApplyLayout(true, layoutChange: true);
        };
        ActualThemeChanged += (_, _) => RefreshMaterial();
        SyncSelection();
    }

    private void WireTab(RadioButton tab)
    {
        tab.Checked += (_, _) =>
        {
            if (_syncing) return;
            int index = Array.IndexOf(_tabs, tab);
            if (index < 0) return;
            if (_pointer is null && !_selection.IsBusy) RequestSelection(index);
            else SyncSelection();
        };
    }

    public void UpdateOptions(string[] options, int columns)
    {
        if (columns < 1 || columns > options.Length) throw new ArgumentOutOfRangeException(nameof(columns));
        int selected = OptionSetPolicy.RemapConfirmed(_optionLabels, SelectedIndex, options);
        var old = _optionLabels.Select((label, i) => (label, cell: new LabelCell(_tabs[i], _captions[i], _foregrounds[i], _selection.IsEnabled(i))))
            .ToDictionary(pair => pair.label, pair => pair.cell, StringComparer.Ordinal);
        var focused = _tabs.FirstOrDefault(tab => tab.FocusState != FocusState.Unfocused);
        FocusState focus = focused?.FocusState ?? FocusState.Unfocused;
        CancelPointer();
        var entered = new List<LabelCell>();
        var cells = options.Select(label =>
        {
            if (old.TryGetValue(label, out var retained)) return retained;
            if (_retiring.Remove(label, out var revived))
            {
                revived.Tab.IsHitTestVisible = true;
                revived.Tab.GroupName = _radioGroup;
                AutomationProperties.SetAccessibilityView(revived.Tab, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
                entered.Add(revived);
                return revived;
            }
            var tabs = new RadioButton[1]; var captions = new TextBlock[1]; var brushes = new SolidColorBrush[1];
            var temporary = CreateLabelLayer(tabs, captions, brushes, [label], _groupName, _fontSize, _compactLabels);
            temporary.Children.Remove(tabs[0]);
            tabs[0].GroupName = _radioGroup;
            WireTab(tabs[0]);
            _labels.Children.Add(tabs[0]);
            var added = new LabelCell(tabs[0], captions[0], brushes[0], true);
            entered.Add(added);
            return added;
        }).ToArray();
        _optionLabels = options.ToArray();
        _columns = columns;
        _tabs = cells.Select(cell => cell.Tab).ToArray();
        _captions = cells.Select(cell => cell.Caption).ToArray();
        _foregrounds = cells.Select(cell => cell.Foreground).ToArray();
        _selection = new(cells.Length);
        for (int i = 0; i < cells.Length; i++) _selection.SetEnabled(i, cells[i].Enabled);
        _selection.Confirm(selected);
        Height = ChoiceTrackLayout.NaturalHeight(options.Length, columns);
        _input.ManipulationMode = options.Length > columns ? ManipulationModes.None : ManipulationModes.System;
        SyncSelection();
        foreach (var pair in old.Where(pair => !options.Contains(pair.Key)).ToArray())
        {
            var cell = pair.Value;
            cell.Tab.IsEnabled = cell.Tab.IsTabStop = cell.Tab.IsHitTestVisible = false;
            cell.Tab.GroupName = "Retiring" + Guid.NewGuid().ToString("N");
            AutomationProperties.SetAccessibilityView(cell.Tab, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            _retiring[pair.Key] = cell;
            FadeLabel(cell, false, () => RemoveRetired(pair.Key, cell));
        }
        ApplyLayout(true, layoutChange: true);
        foreach (var cell in entered) FadeLabel(cell, true);
        if (focused is not null && !_tabs.Contains(focused)) _tabs[selected].Focus(focus);
    }

    private void FadeLabel(LabelCell cell, bool visible, Action? completed = null)
    {
        if (_compositor is null || !_palette.AnimationsEnabled || !LiquidGlassWindow.EffectsEnabled(_window))
        {
            if (_fades.Remove(cell.Tab, out var previous)) previous.Dispose();
            completed?.Invoke();
            return;
        }
        if (!_fades.TryGetValue(cell.Tab, out var fade))
            _fades[cell.Tab] = fade = new TabLabelFade(cell.Tab, entering: visible);
        fade.Show(visible, completed);
    }

    private void RemoveRetired(string key, LabelCell cell)
    {
        if (!_retiring.TryGetValue(key, out var current) || !ReferenceEquals(current, cell) ||
            _optionLabels.Contains(key, StringComparer.Ordinal)) return;
        _retiring.Remove(key);
        if (_fades.Remove(cell.Tab, out var fade)) fade.Dispose();
        _labels.Children.Remove(cell.Tab);
        _labelLayout.UpdateNow();
    }

    internal static Canvas CreateLabelLayer(RadioButton[] tabs, TextBlock[] captions, SolidColorBrush[] foregrounds,
        IReadOnlyList<string>? options = null, string groupName = "性能选项", double fontSize = TrayMenuWindow.ControlCenterButtonFontSize, bool compactLabels = false)
    {
        options ??= new[] { "智能模式", "安静模式", "性能模式", "创作模式" };
        if (options.Count != tabs.Length) throw new ArgumentException("Option/element counts differ.");
        var labels = new Canvas { UseLayoutRounding = false };
        var style = GlassChoiceStyle.Create();
        string group = "YogaITSChoice" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < tabs.Length; i++)
        {
            foregrounds[i] = new SolidColorBrush();
            captions[i] = new TextBlock
            {
                Text = compactLabels && options[i].Length == 4 ? options[i].Insert(2, "\n") : options[i],
                FontSize = fontSize, FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                Foreground = foregrounds[i], UseLayoutRounding = false,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            tabs[i] = new RadioButton
            {
                Content = captions[i], GroupName = group, Style = style,
                Width = 150, Height = 48, MinWidth = 0, MinHeight = 0, UseLayoutRounding = false, Margin = new Thickness(0), Padding = new Thickness(0)
            };
            AutomationProperties.SetName(tabs[i], UiAccessibility.GroupedName(groupName, options[i]));
            labels.Children.Add(tabs[i]);
        }
        return labels;
    }

    internal static void SetLabelAvailability(RadioButton tab, TextBlock caption, bool enabled)
    {
        tab.IsEnabled = enabled;
        // Apply explicitly: unloaded/collapsed controls may defer IsEnabledChanged.
        OptionLabelAppearance.Decorate(caption, enabled);
    }

    internal static void LayoutLabels(RadioButton[] tabs, ChoiceTrackLayout layout)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            var frame = layout.Items[i];
            tabs[i].Width = frame.Width;
            tabs[i].Height = frame.Height;
            if (tabs[i].Content is TextBlock caption) caption.MaxWidth = Math.Max(1, frame.Width - 8);
            Canvas.SetLeft(tabs[i], frame.X);
            Canvas.SetTop(tabs[i], frame.Y);
        }
    }

    private void Connect(object sender, RoutedEventArgs args)
    {
        if (_disposed || _scene is not null || !GlassCapability.Supported) return;
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _scene = _compositor.CreateContainerVisual();
        _track = new(_compositor, track: true);
        _active = new(_compositor, floating: !_useAcceleration);
        if (_useAcceleration)
        {
            _acceleration = new((frame, rest) => PublishReveal(_active, frame, rest), CanAnimate, elasticLayout: true);
            _trackAcceleration = new((frame, rest) => PublishReveal(_track, frame, rest), CanAnimate, layout: true, elasticLayout: true);
        }
        _scene.Children.InsertAtTop(_track.Visual);
        ElementCompositionPreview.SetElementChildVisual(_back, _scene);
        ElementCompositionPreview.SetElementChildVisual(_capsuleLayer, _active.Visual);
        _motionLayer = new GlassMotionLayer(_capsuleLayer, _active, (FrameworkElement)_window.Content);
        if (_acceleration is not null) _acceleration.LayerRequested += _motionLayer.SetRaised;
        _root = XamlRoot;
        if (_root is not null) _root.Changed += RootChanged;
        LiquidGlassWindow.PreferenceChanged += RefreshMaterial;
        LiquidGlassWindow.AvailabilityChanged += RefreshMaterial;
        _palette.ColorValuesChanged += PaletteChanged;
        ApplyLayout(false);
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshMaterial();
    private void PaletteChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshMaterial);

    private void SyncSelection()
    {
        _syncing = true;
        try
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                _tabs[i].IsChecked = i == SelectedIndex;
                SetLabelAvailability(_tabs[i], _captions[i], _selection.IsEnabled(i));
                _tabs[i].IsTabStop = i == _selection.NearestEnabled(SelectedIndex);
                _captions[i].FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }
        finally { _syncing = false; }
    }

    public void SelectIndex(int index, bool animate = false, bool forceSnap = false)
    {
        index = Math.Clamp(index, 0, _tabs.Length - 1);
        bool changed = index != SelectedIndex;
        if (changed && _pointer is not null) CancelPointer();
        bool pending = _selection.IsBusy;
        _selection.Confirm(index);
        SyncSelection();
        if (!changed && !pending && animate && !forceSnap) { RefreshMaterial(); return; }
        ApplyLayout(animate && (changed || pending || forceSnap));
        if (changed) SelectionChanged?.Invoke(index);
    }

    internal void RequestSelection(int index, bool animate = true, bool forceSnap = false)
    {
        if (_selection.IsBusy) return;
        if (!_selection.IsEnabled(index)) return;
        if (SelectionRequested is null || index == SelectedIndex)
        {
            SelectIndex(index, animate, forceSnap);
            return;
        }
        if (!_selection.Request(index)) return;
        SyncSelection();
        ApplyLayout(animate);
        SelectionRequested.Invoke(index);
    }

    public void SetOptionEnabled(int index, bool enabled)
    {
        if (_selection.IsEnabled(index) == enabled) return;
        _selection.SetEnabled(index, enabled);
        SyncSelection();
        RefreshMaterial();
    }

    private bool CanAnimate() => _window is not null && LiquidGlassWindow.EffectsEnabled(_window) && _palette.AnimationsEnabled;

    private void ApplyLayout(bool animate, bool layoutChange = false)
    {
        animate &= _hasPresentedLayout && CanAnimate();
        if (Visibility == Visibility.Visible && ActualWidth > 0 && ActualHeight > 0) _hasPresentedLayout = true;
        if (_fitToContent && _naturalWidths?.Length == _tabs.Length)
        {
            _layout = ChoiceTrackLayout.CreateMeasured(ActualWidth, _naturalWidths);
            double height = _layout.Track.Height + 20;
            if (Height != height) Height = height;
            _input.ManipulationMode = _layout.Rows > 1 ? ManipulationModes.None : ManipulationModes.System;
            foreach (var caption in _captions) { caption.MaxLines = 0; caption.TextTrimming = TextTrimming.None; }
        }
        else _layout = ChoiceTrackLayout.Create(ActualWidth, ActualHeight, _tabs.Length, _columns);
        LayoutLabels(_tabs, _layout);
        _labelLayout.UpdateNow();
        SetReveal(_reveal);
        if (_scene is null || _track is null) return;
        _scene.Size = new((float)ActualWidth, (float)ActualHeight);
        if (_trackAcceleration is not null) _trackAcceleration.SetTarget(_layout.Track, animate && _palette.AnimationsEnabled);
        else _track.SetFrame(_layout.Track, false);
        _acceleration?.ConfigureTrack(_layout, preserveFrame: animate);
        MoveCapsule(_layout.Capsule(_selection.DisplayIndex), animate, layoutChange);
    }

    internal void SetNaturalWidths(double[] widths)
    {
        if (!_fitToContent || _naturalWidths?.SequenceEqual(widths) == true) return;
        _naturalWidths = widths;
        ApplyLayout(true, layoutChange: true);
    }

    internal void SetReveal(double progress)
    {
        _reveal = (float)Math.Clamp(progress, 0, 1.08);
        double opacity = Math.Clamp(progress, 0, 1);
        _back.Opacity = _capsuleLayer.Opacity = _labels.Opacity = opacity;
        for (int i = 0; i < _tabs.Length; i++)
        {
            if (_captions[i].RenderTransform is not TranslateTransform shift)
                _captions[i].RenderTransform = shift = new TranslateTransform();
            var frame = _labelLayout.TryGetFrame(_tabs[i], out var physical) ? physical : _layout.Items[i];
            shift.Y = (_layout.Track.Y - frame.CenterY) * (1 - _reveal);
        }
        if (_track is not null && _trackAcceleration is not null)
            PublishReveal(_track, _trackAcceleration.Physics.Frame, new(_layout.Track.Width, _layout.Track.Height));
        if (_active is not null && _acceleration is not null)
        {
            var rest = _acceleration.Physics.Target;
            PublishReveal(_active, _acceleration.Physics.Frame, new(rest.Width, rest.Height));
        }
    }

    private void PublishReveal(GlassTabSurface surface, TabFrame frame, Vector2 rest)
    {
        float anchor = _layout.Track.Y;
        surface.SetPhysicsFrame(frame with
        {
            Y = anchor + (frame.Y - anchor) * _reveal,
            Height = Math.Max(1, frame.Height * _reveal)
        }, new(rest.X, Math.Max(1, rest.Y * _reveal)));
    }

    private void MoveCapsule(TabFrame frame, bool animate, bool layoutChange = false)
    {
        if (_compositor is null || _active is null) return;
        animate &= CanAnimate();
        if (_acceleration is not null)
        {
            RefreshMaterial();
            _acceleration.SetTarget(frame, animate, layout: layoutChange);
            return;
        }
        if (animate && _state.IsMoving && _active.TryContinueFrame(frame, out var remaining))
        {
            // Releasing at the existing target is not a new movement. Keep the
            // native trajectory, velocity, inertia and its original completion.
            RefreshMaterial(remaining);
            _active.AlignShrinkToRemaining(remaining);
            return;
        }
        var duration = animate ? _active.DurationTo(frame) : TimeSpan.Zero;
        bool moving = animate && duration > TimeSpan.Zero;
        int revision = _state.Begin(moving);
        _batch?.Dispose();
        _batch = null;
        CompositionScopedBatch? positionBatch = null;
        if (!moving) { Frames(); return; }
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch = positionBatch = batch;
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !ReferenceEquals(_batch, batch)) return;
            _batch = null;
            batch.Dispose();
            if (_state.Complete(revision))
            {
                _active.StopMotion();
                RefreshMaterial();
            }
        });
        try { Frames(); }
        finally { batch.End(); }
        void Frames()
        {
            // Scale follows its own duration. Exclude it from arrival accounting,
            // including recovery that can finish later than a shorter movement.
            positionBatch?.Suspend();
            try { RefreshMaterial(duration); }
            finally { positionBatch?.Resume(); }
            _active.SetFrame(frame, animate, positionBatch: positionBatch, movementDuration: duration);
        }
    }

    private void RefreshMaterial() => RefreshMaterial(null);

    private void RefreshMaterial(TimeSpan? arrival)
    {
        if (_track is null || _active is null) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        bool glass = LiquidGlassWindow.EffectsEnabled(_window);
        if (!glass || !_palette.AnimationsEnabled)
        {
            foreach (var pair in _retiring.ToArray()) RemoveRetired(pair.Key, pair.Value);
            foreach (var fade in _fades.Values) fade.Dispose();
            _fades.Clear();
        }
        Color accent = _palette.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        float scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        byte channel = dark ? (byte)255 : (byte)0;
        var neutral = Color.FromArgb(GlassMaterialPolicy.SelectionTintAlpha, channel, channel, channel);
        try
        {
            _track.SetMaterial(glass, Microsoft.UI.Colors.Transparent, Color.FromArgb(24, channel, channel, channel), scale);
            _active.SetMaterial(glass, neutral, Color.FromArgb(24, channel, channel, channel), scale);
            _track.SetMotion(false, false);
            _trackAcceleration?.SetPressed(false, glass && _palette.AnimationsEnabled);
            if (_acceleration is not null) _acceleration.SetPressed(_pointer is not null, glass && _palette.AnimationsEnabled);
            else _active.SetMotion(_pointer is not null, glass && _palette.AnimationsEnabled, arrival);
            for (int i = 0; i < _tabs.Length; i++)
                _foregrounds[i].Color = !_selection.IsEnabled(i) ? OptionLabelAppearance.DisabledColor(dark) :
                    i == SelectedIndex || i == _selection.PendingIndex ? accent : Color.FromArgb(255, channel, channel, channel);
        }
        catch (Exception ex) { CaptureDiagnostics.Write("Floating material: " + ex); }
    }

    private ChoiceTrackLayout InputLayout()
    {
        var frames = _tabs.Select((tab, index) => _labelLayout.TryGetFrame(tab, out var physical)
            ? physical : _layout.Items[index]).ToArray();
        return new(_layout.Track, frames, _layout.Columns);
    }

    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        var inputLayout = InputLayout();
        if (_selection.IsBusy || !_selection.IsEnabled(inputLayout.Hit(point.Position.X, point.Position.Y)) || _pointer is not null || (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            !point.Properties.IsLeftButtonPressed)) return;
        if (!_input.CapturePointer(args.Pointer)) return;
        if (!_gesture.Begin(args.Pointer.PointerId, new Vector2((float)point.Position.X, (float)point.Position.Y), SelectedIndex, inputLayout))
        {
            _input.ReleasePointerCapture(args.Pointer);
            return;
        }
        _pointer = args.Pointer;
        _original = SelectedIndex;
        _tabs[SelectedIndex].Focus(FocusState.Pointer);
        MoveCapsule(_gesture.PreviewFrame, true);
        args.Handled = true;
    }

    private void DragIdleTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_disposed && _pointer is not null && _gesture.IsDragging)
        {
            if (_acceleration is not null) _acceleration.StopPointer();
            else _active?.StopMotion();
        }
    }

    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer?.PointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(this).Position;
        if (_gesture.Move(args.Pointer.PointerId, new Vector2((float)point.X, (float)point.Y), out var frame))
        {
            _state.Begin(false);
            _batch?.Dispose();
            _batch = null;
            if (_acceleration is not null)
            {
                // Physics follows the raw captured pointer. Selection hit testing
                // stays clamped in the gesture and is committed only on release.
                _acceleration.DragTo(frame with
                {
                    X = (float)(point.X - frame.Width / 2),
                    Y = _layout.Rows > 1 ? (float)(point.Y - frame.Height / 2) : frame.Y
                });
            }
            else _active?.SetFrame(frame, false, dragging: true);
            _dragIdle.Stop();
            _dragIdle.Start();
        }
        args.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this).Position;
        if (_pointer?.PointerId != args.Pointer.PointerId ||
            !_gesture.Release(args.Pointer.PointerId, new Vector2((float)point.X, (float)point.Y), out int index)) return;
        var held = _pointer;
        _dragIdle.Stop();
        _pointer = null;
        index = _selection.ReleaseTarget(InputLayout().Hit(point.X, point.Y));
        if (_selection.IsEnabled(index)) RequestSelection(index, forceSnap: true);
        else ApplyLayout(true);
        _input.ReleasePointerCapture(held);
        if (_selection.IsEnabled(index)) _tabs[index].Focus(FocusState.Pointer);
        args.Handled = true;
    }

    public void CancelPreview()
    {
        if (_pointer is null) return;
        CancelPointer();
        ApplyLayout(true);
    }

    private void CancelPointer()
    {
        _dragIdle.Stop();
        _gesture.Cancel();
        _pointer = null;
        _input.ReleasePointerCaptures();
    }

    private void Canceled(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer?.PointerId != args.Pointer.PointerId) return;
        CancelPointer();
        SelectIndex(_original, true, forceSnap: true);
    }

    private void KeyPressed(object sender, KeyRoutedEventArgs args)
    {
        if (_pointer is not null)
        {
            if (args.Key == VirtualKey.Escape)
            {
                CancelPointer();
                SelectIndex(_original, true, forceSnap: true);
                args.Handled = true;
            }
            return;
        }
        if (_selection.IsBusy)
        {
            if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.Space or VirtualKey.Enter)
                args.Handled = true;
            return;
        }
        int index;
        switch (args.Key)
        {
            case VirtualKey.Left: index = _selection.NextEnabled(-1); break;
            case VirtualKey.Right: index = _selection.NextEnabled(1); break;
            case VirtualKey.Up when _layout.Rows > 1:
                index = _selection.NearestEnabled(_layout.VerticalNeighbor(SelectedIndex, -1)); break;
            case VirtualKey.Down when _layout.Rows > 1:
                index = _selection.NearestEnabled(_layout.VerticalNeighbor(SelectedIndex, 1)); break;
            case VirtualKey.Home: index = _selection.NearestEnabled(0); break;
            case VirtualKey.End: index = _selection.NearestEnabled(_tabs.Length - 1); break;
            default: return;
        }
        RequestSelection(index);
        _tabs[index].Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void Disconnect(object sender, RoutedEventArgs args)
    {
        CancelPointer();
        _state.Begin(false);
        _batch?.Dispose();
        _batch = null;
        LiquidGlassWindow.PreferenceChanged -= RefreshMaterial;
        LiquidGlassWindow.AvailabilityChanged -= RefreshMaterial;
        _palette.ColorValuesChanged -= PaletteChanged;
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
        _hasPresentedLayout = false;
        foreach (var fade in _fades.Values) fade.Dispose();
        _fades.Clear();
        foreach (var pair in _retiring.ToArray()) RemoveRetired(pair.Key, pair.Value);
        _acceleration?.Dispose();
        _acceleration = null;
        _trackAcceleration?.Dispose();
        _trackAcceleration = null;
        _motionLayer?.Dispose();
        _motionLayer = null;
        ElementCompositionPreview.SetElementChildVisual(_capsuleLayer, null);
        ElementCompositionPreview.SetElementChildVisual(_back, null);
        _scene?.Children.RemoveAll();
        _active?.Dispose();
        _track?.Dispose();
        _scene?.Dispose();
        _scene = null;
        _track = _active = null;
        _compositor = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dragIdle.Tick -= DragIdleTick;
        Disconnect(this, new RoutedEventArgs());
    }
}
