using YogaITS.Tray.UI.Animation;
using YogaITS.Tray.UI.Layout;
#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Animation;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace YogaITS.Tray.UI.Controls;

// Prepare hidden templates and geometry without connecting their material early.
// One acceleration state drives occupied height and glass contours together.
internal sealed class AnimatedOptionHost : Grid, IDisposable
{
    private readonly Func<bool> _canAnimate;
    private readonly Action<double> _reveal;
#if LIQUID_GLASS
    private readonly AccelerationTabMotion _motion;
#endif
    private readonly Canvas _canvas = new();
    private FrameworkElement? _child;
    private double _naturalHeight, _progress = 1;
    private bool _expanded = true, _disposed, _templatePrepared;
#if LIQUID_GLASS
    private bool _finishing;
#endif
    private StackPanel? _parent;
    private WarmStackSpacing? _spacing;
    internal double OccupiedFraction=>_progress;
    public AnimatedOptionHost(Func<bool> canAnimate, Action<double> reveal)
    {
        _canAnimate = canAnimate; _reveal = reveal;
        Children.Add(_canvas);
        SizeChanged += HostSizeChanged;
#if LIQUID_GLASS
        _motion = new AccelerationTabMotion((frame, _) => Apply(frame.X / 1000.0),
            () => _canAnimate() && SettingsMotion.AnimationsEnabled, layout: true);
        _motion.SetTarget(new(1000, 0, 48, 48), false);
        // The normalized phase has finite endpoints, just like a tab track.
        _motion.Physics.SetHorizontalBounds(0, 1048);
#endif
        Loaded += LoadedHost;
        Unloaded += UnloadedHost;
        GettingFocus += BlockHiddenFocus;
    }
    public void SetChild(FrameworkElement child)
    {
        if (_child is not null) _child.SizeChanged -= ChildSizeChanged;
        _canvas.Children.Clear(); _canvas.Children.Add(child); _child = child;
        child.SizeChanged += ChildSizeChanged;
        _templatePrepared=false; Prepare();
    }
    private void HostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if(args.PreviousSize.Width!=args.NewSize.Width) RefreshNaturalSize();
    }
    private void ChildSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Height <= 0 || Math.Abs(_naturalHeight - args.NewSize.Height) < 0.001) return;
        _naturalHeight = args.NewSize.Height+(_child?.Margin.Top??0)+(_child?.Margin.Bottom??0);
        _canvas.Height = _naturalHeight * _progress;
    }
    internal void Attach(StackPanel parent)
    {
        if(!ReferenceEquals(_parent,parent))
        {
            DetachParent(); _parent=parent;
            parent.Loaded+=ParentLoaded;
            parent.SizeChanged+=ParentSizeChanged;
        }
        _spacing=WarmStackSpacing.Attach(parent);
        Prepare();
    }
    private void DetachParent()
    {
        if(_parent is null) return;
        _parent.Loaded-=ParentLoaded;
        _parent.SizeChanged-=ParentSizeChanged;
        _parent=null;
    }
    private void ParentLoaded(object sender,RoutedEventArgs args)=>Prepare();
    private void ParentSizeChanged(object sender,SizeChangedEventArgs args)
    {
        if(args.PreviousSize.Width!=args.NewSize.Width) Prepare();
    }
    private void LoadedHost(object sender,RoutedEventArgs args)=>Prepare();
    private void BlockHiddenFocus(UIElement sender,GettingFocusEventArgs args)
    {
        if(!_expanded) args.TryCancel();
    }
    private void Prepare()
    {
        if(_child is null) return;
        // ApplyTemplate does not attach or refresh glass. Loaded/Unloaded remain
        // the sole owners of renderer connection, as for an ordinary card.
        if(!_templatePrepared) { WarmUiTree.ApplyTemplates(_child); _templatePrepared=true; }
        OptionGroupPresentation.RefreshTree(_child);
        RefreshNaturalSize();
        _spacing?.Refresh();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        // Canvas does not measure its children for its own desired size. Feed
        // it the child's height at the actual width, including before Loaded.
        RefreshNaturalSize(availableSize.Width, arrange: false);
        return base.MeasureOverride(availableSize);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        RefreshNaturalSize(finalSize.Width);
        return base.ArrangeOverride(finalSize);
    }
    private void RefreshNaturalSize(double? availableWidth = null, bool arrange = true)
    {
        if (_child is null) return;
        double width=availableWidth ?? (_parent?.ActualWidth>0
            ? Math.Max(0,_parent.ActualWidth-Margin.Left-Margin.Right)
            : ActualWidth>0?ActualWidth:double.PositiveInfinity);
        if(double.IsFinite(width))
        {
            double content=Math.Max(0,width-_child.Margin.Left-_child.Margin.Right);
            if(_child.Width!=content) _child.Width=content;
        }
        _child.Measure(new Size(width,double.PositiveInfinity));
        if(_child.DesiredSize.Height>0) _naturalHeight=_child.DesiredSize.Height;
        if(arrange && double.IsFinite(width) && _naturalHeight>0)
            _child.Arrange(new Rect(0,0,width,_naturalHeight));
        _canvas.Height=_naturalHeight*_progress;
    }
    public void SetExpanded(bool expanded)
    {
        if (_disposed || _expanded == expanded) return;
        _expanded = expanded;
        IsHitTestVisible = expanded;
        if(expanded) Visibility=Visibility.Visible;
        Prepare();
        RefreshMotion();
    }
    public void RefreshMotion()
    {
        if (_disposed) return;
        RefreshNaturalSize();
#if LIQUID_GLASS
        bool animate = _canAnimate() && SettingsMotion.AnimationsEnabled;
        _motion.SetPressed(false, animate);
        // Use the same neutral response as card/capsule inflation, independent
        // of the number of rows. Retargeting keeps the current momentum.
        _motion.SetTarget(new(_expanded ? 1000 : 0, 0, 48, 48), animate);
#else
        Apply(_expanded ? 1 : 0);
#endif
    }
    private void Apply(double progress)
    {
        if (_disposed) return;
#if LIQUID_GLASS
        float endpoint = _expanded ? 1000 : 0;
        bool targetMatches = _motion is not null && _motion.Physics.Target.X == endpoint;
        bool reached = _expanded ? progress >= 1 : progress <= 0;
        // The occupied area is a scalar phase: once its leading edge reaches
        // the endpoint, stop the entire phase, including the unused trailing edge.
        // Otherwise that edge's recoil would delay hiding an already empty bar.
        if (!_finishing && targetMatches && reached && !_motion!.Physics.IsSettled)
        {
            _finishing = true;
            try { _motion.SetTarget(new(endpoint, 0, 48, 48), false); }
            finally { _finishing = false; }
            return;
        }
        if (targetMatches && _motion!.Physics.IsSettled) progress = _expanded ? 1 : 0;
#endif
        double next = Math.Clamp(progress, 0, 1);
        if (Math.Abs(next - _progress) > 0.00001)
        {
            _progress = next;
            _reveal(next);
            _canvas.Height = _naturalHeight * next;
            _spacing?.Refresh();
        }
        // Use normal XAML visibility for the renderer lifecycle. Never put a
        // persistent opacity/clip isolation layer around backdrop effects.
        Visibility=_progress>0 || _expanded?Visibility.Visible:Visibility.Collapsed;
    }
    private void UnloadedHost(object sender, RoutedEventArgs args)
    {
#if LIQUID_GLASS
        _motion.SetPressed(false, false);
#endif
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachParent();
        GettingFocus -= BlockHiddenFocus;
        Loaded -= LoadedHost;
        Unloaded -= UnloadedHost;
        SizeChanged -= HostSizeChanged;
        if (_child is not null) _child.SizeChanged -= ChildSizeChanged;
#if LIQUID_GLASS
        _motion.Dispose();
#endif
    }
}
