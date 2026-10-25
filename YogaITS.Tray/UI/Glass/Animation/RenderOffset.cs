using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace YogaITS.Tray.UI.Glass.Animation;

// Render transforms do not change layout and remain visible to TransformToVisual,
// keeping the root glass overlay aligned with moving text and parent groups.
internal sealed class RenderOffset : IDisposable
{
    private readonly FrameworkElement _element;
    private readonly Transform? _original;
    private readonly TransformGroup _group = new();
    private readonly TranslateTransform _translation = new();
    public Vector2 Value => new((float)_translation.X, (float)_translation.Y);
    public RenderOffset(FrameworkElement element)
    {
        _element = element;
        _original = element.RenderTransform;
        if (_original is not null) _group.Children.Add(_original);
        _group.Children.Add(_translation);
        element.RenderTransform = _group;
    }
    public void Set(Vector2 offset)
    {
        if (Math.Abs(_translation.X - offset.X) > 0.001) _translation.X = offset.X;
        if (Math.Abs(_translation.Y - offset.Y) > 0.001) _translation.Y = offset.Y;
    }
    public void Dispose()
    {
        if (_element.RenderTransform == _group) _element.RenderTransform = _original;
    }
}
