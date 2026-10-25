using System.Numerics;
using Microsoft.UI.Composition;

namespace YogaITS.Tray.UI.Glass.Rendering;

// A second draw of the SAME material/physical geometry, not a second capture
// or shader graph. Never remove a connected visual from XAML's ownership.
internal sealed class GlassSurfaceOverlay : IDisposable
{
    public ContainerVisual Visual { get; }
    private readonly ContainerVisual _body;
    private readonly SpriteVisual _paint;
    private bool _disposed;
    public GlassSurfaceOverlay(Visual source, Visual body, SpriteVisual paint)
    {
        var brush = paint.Brush ?? throw new InvalidOperationException("Glass material is not connected.");
        var compositor = source.Compositor;
        Visual = compositor.CreateContainerVisual();
        _body = compositor.CreateContainerVisual();
        _body.RelativeSizeAdjustment = Vector2.One;
        _paint = compositor.CreateSpriteVisual();
        _paint.RelativeSizeAdjustment = Vector2.One;
        _paint.Brush = brush;
        _paint.Shadow = paint.Shadow;
        _body.Children.InsertAtTop(_paint);
        Visual.Children.InsertAtTop(_body);
        foreach (string property in new[] { "Offset", "Size", "Scale", "CenterPoint", "TransformMatrix" })
            Follow(Visual, property, source);
        Follow(_body, "TransformMatrix", body);
        Follow(_paint, "Opacity", paint);
    }
    private static void Follow(CompositionObject destination, string property, CompositionObject source)
    {
        using var expression = destination.Compositor.CreateExpressionAnimation("S." + property);
        expression.SetReferenceParameter("S", source);
        destination.StartAnimation(property, expression);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string property in new[] { "Offset", "Size", "Scale", "CenterPoint", "TransformMatrix" }) Visual.StopAnimation(property);
        _body.StopAnimation("TransformMatrix");
        _paint.StopAnimation("Opacity");
        _paint.Brush = null;
        _paint.Shadow = null;
        _body.Children.RemoveAll();
        Visual.Children.RemoveAll();
        _paint.Dispose();
        _body.Dispose();
        Visual.Dispose();
    }
}
