using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml.Media;
using YogaITS.Tray.Demos.SwiftTabs;

namespace YogaITS.Tray.Demos.Acceleration;

// Publish only the latest physical rectangle; share the glass material/capture.
internal sealed class AccelerationTabMotion : IDisposable
{
    private readonly Action<TabFrame, Vector2> _publish;
    internal FourEdgePhysics Physics { get; }
    private bool _enabled, _rendering, _disposed, _raised;
    private double _lastTime;
    public event Action<bool>? LayerRequested;
    public event Action<TabFrame, Vector2>? FramePublished;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public AccelerationTabMotion(GlassTabSurface surface, bool layout = false, bool elasticLayout = false) : this(surface.SetPhysicsFrame, layout, elasticLayout) { }
    public AccelerationTabMotion(Action<TabFrame, Vector2> publish, bool layout = false, bool elasticLayout = false)
    {
        _publish = publish;
        Physics = new(layout, elasticLayout);
        Physics.Reset(new(6, 16, 140, 48));
    }
    public void ConfigureTrack(SwiftTabLayout layout, bool preserveFrame = false)
    {
        AdvanceToNow();
        if (preserveFrame && Physics.Dragging) Physics.SetTarget(Physics.Target);
        Physics.ConfigureTrack(layout.Track, layout.Capsule(0), layout.Capsule(layout.Items.Length - 1), constrainVertical: layout.Rows > 1, preserveFrame: preserveFrame);
        Publish();
    }
    public void SetPressed(bool pressed, bool enabled)
    {
        if (_enabled == enabled && Physics.Pressed == (pressed && enabled)) return;
        bool wasPressed = Physics.Pressed, wasRaised = _raised;
        AdvanceToNow();
        _enabled = enabled;
        Physics.SetPressed(pressed && enabled);
        if (!enabled) { Physics.Reset(Physics.Target); Stop(); Publish(); }
        else Wake();
        if (pressed && enabled && !wasPressed && wasRaised && _raised) LayerRequested?.Invoke(true);
    }
    public void SetTarget(TabFrame frame, bool animate, bool? layout = null)
    {
        AdvanceToNow();
        if (!animate || !_enabled) { Physics.Reset(frame); Stop(); }
        else { Physics.SetTarget(frame, layout); Wake(); }
        Publish();
    }
    public void DragTo(TabFrame frame)
    {
        double now = AdvanceToNow();
        if (!_enabled) Physics.Reset(frame);
        else { Physics.DragTo(frame, now); Wake(); }
        Publish();
    }
    public void StopPointer() { AdvanceToNow(); Physics.StopPointer(); Wake(); }
    private double AdvanceToNow()
    {
        double now = Now;
        if (_rendering) Physics.Advance(now - _lastTime);
        _lastTime = now;
        return now;
    }
    private void Publish()
    {
        var frame = Physics.Frame;
        var rest = new Vector2(Physics.Target.Width, Physics.Target.Height);
        _publish(frame, rest);
        FramePublished?.Invoke(frame, rest);
    }
    private void Wake()
    {
        if (_disposed || !_enabled) return;
        if(Physics.IsSettled) { Stop(); return; }
        if (!_rendering)
        {
            _rendering = true;
            _lastTime = Now;
            CompositionTarget.Rendering += Render;
        }
        UpdateLayer();
    }
    private void Render(object? sender, object args)
    {
        if (_disposed) return;
        AdvanceToNow();
        Publish();
        if (Physics.IsSettled) Stop();
    }
    private void Stop()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= Render;
            _rendering = false;
        }
        UpdateLayer();
    }
    private void UpdateLayer()
    {
        bool raised = !_disposed && _enabled && (Physics.Pressed || (_rendering && !Physics.IsSettled));
        if (_raised == raised) return;
        _raised = raised;
        LayerRequested?.Invoke(raised);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
}
