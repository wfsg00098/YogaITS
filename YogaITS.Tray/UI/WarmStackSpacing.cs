using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YogaITS.Tray.UI;

// Express native spacing as margins weighted by the animated occupied height.
internal sealed class WarmStackSpacing
{
    private static readonly ConditionalWeakTable<StackPanel,WarmStackSpacing> Owners=new();
    private readonly StackPanel _panel;
    private readonly double _spacing;
    private sealed class Entry(Thickness original) { public Thickness Original=original; public int Seen; }
    private readonly Dictionary<FrameworkElement,Entry> _margins=[];
    private readonly List<FrameworkElement> _removed=[];
    private bool _updating;
    private int _scan;
    private WarmStackSpacing(StackPanel panel)
    {
        _panel=panel; _spacing=panel.Spacing;
        panel.LayoutUpdated+=(_,_)=>Refresh();
    }
    public static WarmStackSpacing Attach(StackPanel panel)
    {
        var owner=Owners.GetValue(panel,value=>new(value)); owner.Refresh(); return owner;
    }
    public void Refresh()
    {
        if(_updating) return; _updating=true;
        try
        {
            if(_panel.Spacing!=0) _panel.Spacing=0;
            int scan=++_scan; double preceding=0;
            foreach(var item in _panel.Children)
            {
                if(item is not FrameworkElement child) continue;
                if(!_margins.TryGetValue(child,out var entry)) _margins[child]=entry=new(child.Margin);
                entry.Seen=scan;
                double shown=child.Visibility==Visibility.Collapsed?0:child is AnimatedOptionHost host?host.OccupiedFraction:1;
                var margin=entry.Original;
                margin.Top+=_spacing*shown*preceding;
                if(child.Margin!=margin) child.Margin=margin;
                preceding=Math.Clamp(preceding+shown,0,1);
            }
            _removed.Clear();
            foreach(var pair in _margins) if(pair.Value.Seen!=scan) _removed.Add(pair.Key);
            foreach(var child in _removed) { child.Margin=_margins[child].Original; _margins.Remove(child); }
            _removed.Clear();
        }
        finally { _updating=false; }
    }
}
