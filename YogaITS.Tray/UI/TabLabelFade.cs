using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace YogaITS.Tray.UI;

// Opacity is independent of physical geometry. The base value always matches
// the requested visibility, so delayed UI completions cannot leave an active
// label transparent. Reversals continue from the current estimated opacity.
internal sealed class TabLabelFade : IDisposable
{
    private readonly FrameworkElement _element;
    private readonly Visual _visual;
    private const double DurationSeconds = 0.1;
    private CompositionScopedBatch? _batch;
    private Action? _completion;
    private int _revision;
    private bool _disposed;
    private float _from, _target;
    private double _started;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public TabLabelFade(FrameworkElement element, bool entering)
    {
        _element = element;
        _visual = ElementCompositionPreview.GetElementVisual(element);
        _from = _target = entering ? 0 : _visual.Opacity;
        _started = Now;
        _visual.Opacity = _target;
    }
    public void Show(bool visible, Action? completed = null)
    {
        if (_disposed) return;
        double now = Now;
        _from += (_target - _from) * (float)Math.Clamp((now - _started) / DurationSeconds, 0, 1);
        _target = visible ? 1 : 0;
        _started = now;
        int revision = ++_revision;
        _completion = completed;
        _batch?.Dispose();
        _batch = null;
        _visual.StopAnimation("Opacity");
        _visual.Opacity = _target;
        var batch = _visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch = batch;
        batch.Completed += (_, _) => _element.DispatcherQueue.TryEnqueue(() => Finish(revision, batch));
        using var fade = _visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromSeconds(DurationSeconds);
        fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        using var linear = _visual.Compositor.CreateLinearEasingFunction();
        fade.InsertKeyFrame(0, _from, linear);
        fade.InsertKeyFrame(1, _target, linear);
        _visual.StartAnimation("Opacity", fade);
        batch.End();
    }
    private void Finish(int revision, CompositionScopedBatch batch)
    {
        if (_disposed || revision != _revision || !ReferenceEquals(_batch, batch)) return;
        _batch = null;
        batch.Dispose();
        _visual.StopAnimation("Opacity");
        _visual.Opacity = _target;
        _from = _target;
        var completed = _completion;
        _completion = null;
        completed?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_revision;
        _completion = null;
        _batch?.Dispose();
        _batch = null;
        _visual.StopAnimation("Opacity");
        _visual.Opacity = 1;
    }
}
