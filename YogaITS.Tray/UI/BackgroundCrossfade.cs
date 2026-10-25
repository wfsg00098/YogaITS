using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Windows.UI;
using Windows.Graphics.Effects;

namespace YogaITS.Tray.UI;

// UI-owned readiness gate and composition-only blending. Producers remain
// alive until no visible branch samples them; obsolete pending work is released.
internal sealed class BackgroundCrossfade : IDisposable
{
    internal abstract class Node : IDisposable
    {
        public abstract CompositionBrush Brush { get; }
        public event Action? Changed;
        protected void Notify()=>Changed?.Invoke();
        public abstract void Dispose();
    }
    internal sealed class Layer(CompositionBrush brush,Action release) : Node
    {
        private bool _disposed;
        public override CompositionBrush Brush=>brush;
        public override void Dispose()
        {
            if(_disposed) return; _disposed=true; release();
        }
    }
    private sealed class Blend : Node
    {
        private Node? _previous;
        private readonly Layer _next;
        private CompositionEffectBrush? _effect;
        private CompositionScopedBatch? _batch;
        private readonly CompositionPropertySet _progress;
        private bool _disposed;
        public override CompositionBrush Brush=>_effect??_next.Brush;
        public Blend(CompositionEffectFactory factory,Node previous,Layer next,Func<Action,bool> post)
        {
            _previous=previous; _next=next;
            _effect=factory.CreateBrush();
            _effect.SetSourceParameter("Previous",previous.Brush);
            _effect.SetSourceParameter("Next",next.Brush);
            previous.Changed+=PreviousChanged;
            _progress=factory.Compositor.CreatePropertySet();
            _progress.InsertScalar("Value",0);
            // One progress clock drives complementary, explicitly named weights.
            // Bind before opening the finite batch; expressions have no endpoint.
            BindWeight("Blend.Source1Amount","1-P.Value");
            BindWeight("Blend.Source2Amount","P.Value");
            _batch=factory.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _batch.Completed+=(_,_)=>post(Complete);
            using var animation=factory.Compositor.CreateScalarKeyFrameAnimation();
            animation.Duration=TimeSpan.FromMilliseconds(220);
            animation.StopBehavior=AnimationStopBehavior.SetToFinalValue;
            using var easing=factory.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f,0),new Vector2(.2f,1));
            animation.InsertKeyFrame(0,0);
            animation.InsertKeyFrame(1,1,easing);
            _progress.StartAnimation("Value",animation);
            _batch.End();
        }
        private void BindWeight(string property,string expression)
        {
            using var animation=_progress.Compositor.CreateExpressionAnimation(expression);
            animation.SetReferenceParameter("P",_progress);
            _effect!.Properties.StartAnimation(property,animation);
        }
        private void PreviousChanged()
        {
            if(!_disposed && _effect is not null && _previous is not null)
                _effect.SetSourceParameter("Previous",_previous.Brush);
        }
        internal void Complete()
        {
            if(_disposed || _effect is null) return;
            var effect=_effect;
            // The terminal mixed pixels and the direct source MUST be identical.
            effect.Properties.StopAnimation("Blend.Source1Amount");
            effect.Properties.StopAnimation("Blend.Source2Amount");
            effect.Properties.InsertScalar("Blend.Source1Amount",0);
            effect.Properties.InsertScalar("Blend.Source2Amount",1);
            _progress.StopAnimation("Value");
            _progress.InsertScalar("Value",1);
            _effect=null;
            // Rebind every parent (or the visible output) before releasing its
            // old producer. Nested transitions may complete in either order.
            Notify();
            var previous=_previous; _previous=null;
            if(previous is not null) previous.Changed-=PreviousChanged;
            effect.Dispose();
            previous?.Dispose();
            _batch?.Dispose(); _batch=null;
        }
        public override void Dispose()
        {
            if(_disposed) return; _disposed=true;
            if(_previous is not null) _previous.Changed-=PreviousChanged;
            _effect?.Properties.StopAnimation("Blend.Source1Amount");
            _effect?.Properties.StopAnimation("Blend.Source2Amount");
            _effect?.Dispose(); _effect=null;
            _progress.StopAnimation("Value"); _progress.Dispose();
            _batch?.Dispose(); _batch=null;
            _previous?.Dispose(); _previous=null;
            _next.Dispose();
        }
    }
    private readonly CompositionEffectFactory _factory;
    private readonly Func<Action,bool> _post;
    private readonly Action<CompositionBrush> _publish;
    private Node _current;
    private Layer? _pending;
    private bool _disposed;
    public CompositionBrush Brush=>_current.Brush;
    public BackgroundCrossfade(Compositor compositor,Color initial,Func<Action,bool> post,Action<CompositionBrush> publish)
    {
        _post=post; _publish=publish;
        using var effect=CreateEffect(new CompositionEffectSourceParameter("Previous"),new CompositionEffectSourceParameter("Next"));
        _factory=compositor.CreateEffectFactory(effect,["Blend.Source1Amount","Blend.Source2Amount"]);
        var solid=compositor.CreateColorBrush(initial);
        _current=new Layer(solid,solid.Dispose);
        _current.Changed+=Publish;
    }
    internal static ArithmeticCompositeEffect CreateEffect(IGraphicsEffectSource previous,IGraphicsEffectSource next)=>new()
    {
        Name="Blend",MultiplyAmount=0,Offset=0,Source1Amount=1,Source2Amount=0,
        Source1=previous,Source2=next
    };
    public Layer Stage(CompositionBrush brush,Action release)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        CancelPending();
        return _pending=new Layer(brush,release);
    }
    public void CancelPending()
    {
        var pending=_pending; _pending=null; pending?.Dispose();
    }
    public void Present(Layer layer,bool animate)
    {
        if(_disposed || !ReferenceEquals(_pending,layer)) return;
        var previous=_current;
        Node next=animate?new Blend(_factory,previous,layer,_post):layer;
        _pending=null;
        previous.Changed-=Publish;
        _current=next; next.Changed+=Publish;
        Publish();
        if(!animate) previous.Dispose();
    }
    private void Publish()
    {
        if(!_disposed) _publish(_current.Brush);
    }
    // Also used by the presentation-less composition checks: real batches have
    // no clock there, so never treat their timing as a visible-window result.
    internal void CompleteCurrent()
    {
        if(_current is Blend blend) blend.Complete();
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        CancelPending(); _current.Changed-=Publish; _current.Dispose(); _factory.Dispose();
    }
}
