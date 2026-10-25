using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using YogaITS.Tray.Demos.Acceleration;
using YogaITS.Tray.Demos.SwiftTabs;

namespace YogaITS.Tray.UI;

// The same force integrator moves layout children without animating Measure or
// Arrange. Parent motion is inherited once; glass size has its own four edges.
internal sealed class AccelerationLayoutMotion
{
    private static readonly ConditionalWeakTable<Panel, AccelerationLayoutMotion> Owners = new();
    private readonly Panel _panel;
    private readonly bool _centers;
    private readonly Dictionary<FrameworkElement, Entry> _entries = [];
    private bool _enabled, _updating, _nativeTransitions;
    private int _scan;
    private readonly List<FrameworkElement> _removed=[];
    private bool? _lastNative;
    private AccelerationLayoutMotion(Panel panel, bool centers)
    {
        _panel = panel;
        _centers = centers;
        panel.Loaded += (_, _) => Refresh();
        panel.Unloaded += (_, _) => Clear();
        panel.LayoutUpdated += (_, _) => UpdateNow();
    }
    public static AccelerationLayoutMotion Attach(Panel panel, bool centers = false, bool nativeTransitions = false)
    {
        var owner = Owners.GetValue(panel, value => new(value, centers));
        owner._nativeTransitions |= nativeTransitions;
        owner.Refresh();
        return owner;
    }
    public static void RefreshTree(DependencyObject node)
    {
        if (node is Panel panel && Owners.TryGetValue(panel, out var owner)) owner.Refresh();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) RefreshTree(VisualTreeHelper.GetChild(node, i));
    }
    public void Refresh()
    {
        bool enabled = LiquidGlassWindow.AcceleratesLayout(_panel);
        if (_enabled != enabled)
        {
            Clear();
            _enabled = enabled;
        }
        bool native = !enabled && SettingsMotion.AnimationsEnabled;
        if (_nativeTransitions && _lastNative != native)
        {
            _lastNative = native;
            _panel.ChildrenTransitions ??= new TransitionCollection();
            for (int i = _panel.ChildrenTransitions.Count - 1; i >= 0; i--)
                if (_panel.ChildrenTransitions[i] is RepositionThemeTransition) _panel.ChildrenTransitions.RemoveAt(i);
            if (native)
                _panel.ChildrenTransitions.Add(new RepositionThemeTransition { IsStaggeringEnabled = false });
        }
        UpdateNow();
    }
    public void UpdateNow()
    {
        if (!_enabled || _updating || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0) return;
        _updating = true;
        try
        {
            int scan=++_scan;
            foreach(var item in _panel.Children)
            {
                if(item is not FrameworkElement child || child.Visibility!=Visibility.Visible) continue;
                bool exists=_entries.TryGetValue(child,out var entry);
                if(exists) entry!.Seen=scan;
                TabFrame target;
                if (_panel is Canvas && _centers && double.IsFinite(child.Width) && double.IsFinite(child.Height))
                {
                    double x = Canvas.GetLeft(child), y = Canvas.GetTop(child);
                    target = new((float)(double.IsFinite(x) ? x : 0), (float)(double.IsFinite(y) ? y : 0),
                        (float)child.Width, (float)child.Height);
                }
                else
                {
                    if (child.ActualWidth <= 0 || child.ActualHeight <= 0) continue;
                    var origin=child.ActualOffset;
                    target=new(origin.X-24,origin.Y-24,48,48);
                }
                if (!float.IsFinite(target.X + target.Y + target.Width + target.Height)) continue;
                if (!exists) _entries[child] = new Entry(child, target) {Seen=scan};
                else entry!.Move(target);
            }
            _removed.Clear();
            foreach(var pair in _entries) if(pair.Value.Seen!=scan) _removed.Add(pair.Key);
            foreach(var child in _removed) { _entries[child].Dispose(); _entries.Remove(child); }
            _removed.Clear();
        }
        finally { _updating = false; }
    }
    public bool TryGetFrame(FrameworkElement child, out TabFrame frame)
    {
        if (_centers && _entries.TryGetValue(child, out var entry)) { frame = entry.Frame; return true; }
        frame = default;
        return false;
    }
    private void Clear()
    {
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
        _enabled = false;
    }
    private sealed class Entry : IDisposable
    {
        private readonly RenderOffset _offset;
        private readonly AccelerationTabMotion _motion;
        private TabFrame _target;
        public int Seen { get; set; }
        public TabFrame Frame => _motion.Physics.Frame;
        public Entry(FrameworkElement child, TabFrame target)
        {
            _offset = new RenderOffset(child);
            _target = target;
            _motion = new AccelerationTabMotion((frame, _) => _offset.Set(new Vector2(frame.Center - _target.Center, frame.CenterY - _target.CenterY)), layout: true, elasticLayout: true);
            _motion.SetTarget(target, false);
            _motion.SetPressed(false, true);
        }
        public void Move(TabFrame target)
        {
            if (Math.Abs(target.X - _target.X) + Math.Abs(target.Y - _target.Y) +
                Math.Abs(target.Width - _target.Width) + Math.Abs(target.Height - _target.Height) < 0.001) return;
            _target = target;
            _motion.SetTarget(target, true);
        }
        public void Dispose() { _motion.Dispose(); _offset.Dispose(); }
    }
}
