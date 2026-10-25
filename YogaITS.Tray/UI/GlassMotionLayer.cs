using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using YogaITS.Tray.Demos.SwiftTabs;

namespace YogaITS.Tray.UI;

// Keep the original visual attached to XAML. A temporary root draw shares its
// brush and geometry while XAML still owns layout, focus and hit testing.
internal sealed class GlassMotionLayer : IDisposable
{
    private static readonly ConditionalWeakTable<FrameworkElement, RootLayer> Roots = new();
    private readonly FrameworkElement _host;
    private readonly GlassTabSurface _surface;
    private readonly RootLayer _root;
    private ContainerVisual? _floating;
    private GlassSurfaceOverlay? _overlay;
    private float _restOpacity = 1;
    private bool _disposed, _unavailable;
    public bool IsRaised => _floating is not null;
    public GlassMotionLayer(FrameworkElement host, GlassTabSurface surface, FrameworkElement root)
    {
        _host = host;
        _surface = surface;
        _root = Roots.GetValue(root, element => new RootLayer(element));
    }
    public void SetRaised(bool raised)
    {
        if (_disposed) return;
        if (!raised) { Return(); return; }
        if (_unavailable) return;
        try
        {
            if (_floating is not null)
            {
                _root.Visual.Children.Remove(_floating);
                _root.Visual.Children.InsertAtTop(_floating);
                return;
            }
            _restOpacity = _surface.Visual.Opacity;
            _overlay = _surface.CreateOverlayVisual();
            _floating = _root.Visual.Compositor.CreateContainerVisual();
            _floating.Children.InsertAtTop(_overlay.Visual);
            _root.Visual.Children.InsertAtTop(_floating);
            _surface.Visual.Opacity = 0;
            _root.Add(this);
            UpdatePosition();
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException) { Fail(ex); }
    }
    private void Fail(Exception error)
    {
        _unavailable = true;
        Return();
        CaptureDiagnostics.Write("Glass top-layer feedback unavailable; retained original surface: " + error);
    }
    private void UpdatePosition()
    {
        if (_floating is null) return;
        if (_host.XamlRoot != _root.Root.XamlRoot) { Return(); return; }
        var origin = _host.TransformToVisual(_root.Root).TransformPoint(default);
        var offset=new Vector3((float)origin.X,(float)origin.Y,0);
        var size=new Vector2((float)_host.ActualWidth,(float)_host.ActualHeight);
        float opacity=(float)_host.Opacity*_restOpacity;
        if(_floating.Offset!=offset) _floating.Offset=offset;
        if(_floating.Size!=size) _floating.Size=size;
        if(_floating.Opacity!=opacity) _floating.Opacity=opacity;
    }
    private void Return()
    {
        if (_floating is null && _overlay is null) return;
        _surface.Visual.Opacity = _restOpacity;
        _root.Remove(this);
        if (_floating?.Parent is ContainerVisual parent) parent.Children.Remove(_floating);
        _floating?.Children.RemoveAll();
        _overlay?.Dispose();
        _overlay = null;
        _floating?.Dispose();
        _floating = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Return();
        _disposed = true;
    }
    private sealed class RootLayer
    {
        public FrameworkElement Root { get; }
        public ContainerVisual Visual { get; }
        private readonly HashSet<GlassMotionLayer> _active = [];
        public RootLayer(FrameworkElement root)
        {
            Root = root;
            if (ElementCompositionPreview.GetElementChildVisual(root) is not null)
                throw new InvalidOperationException("Window already owns a root composition overlay.");
            Visual = ElementCompositionPreview.GetElementVisual(root).Compositor.CreateContainerVisual();
            Visual.RelativeSizeAdjustment = Vector2.One;
            ElementCompositionPreview.SetElementChildVisual(root, Visual);
            root.Unloaded += Unloaded;
        }
        public void Add(GlassMotionLayer layer)
        {
            if (_active.Count == 0) CompositionTarget.Rendering += Render;
            _active.Add(layer);
        }
        public void Remove(GlassMotionLayer layer)
        {
            _active.Remove(layer);
            if (_active.Count == 0) CompositionTarget.Rendering -= Render;
        }
        private void Render(object? sender, object args)
        {
            foreach (var layer in _active.ToArray())
                try { layer.UpdatePosition(); }
                catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException) { layer.Fail(ex); }
        }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            foreach (var layer in _active.ToArray()) layer.Return();
            CompositionTarget.Rendering -= Render;
            ElementCompositionPreview.SetElementChildVisual(Root, null);
            Root.Unloaded -= Unloaded;
            Visual.Dispose();
            Roots.Remove(Root);
        }
    }
}
