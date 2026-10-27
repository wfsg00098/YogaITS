using YogaITS.Tray.UI.Shell;
#if LIQUID_GLASS
using YogaITS.Tray.UI.Glass.Controls;
#endif
using Microsoft.UI.Xaml;

namespace YogaITS.Tray.UI.Controls;

// One semantic control used by both builds. The optional renderer never leaks
// into window/feature definitions; native cards bind to this same contract.
internal sealed class ChoiceBar : IDisposable
{
#if LIQUID_GLASS
    private readonly GlassOptionBar _glass;
    public UIElement? View => _glass;
    public int SelectedIndex => _glass.SelectedIndex;
    public int OptionCount => _glass.OptionCount;
    public bool IsPending => _glass.IsPending;
#else
    private OptionSelectionState _selection;
    private string[] _labels;
    public UIElement? View => null;
    public int SelectedIndex => _selection.SelectedIndex;
    public int OptionCount => _selection.Count;
    public bool IsPending => _selection.IsBusy;
#endif
    public event Action<int>? SelectionRequested;
    public event Action<int>? SelectionChanged;

    public ChoiceBar(Window window, IReadOnlyList<string>? options = null,
        string groupName = "性能选项", double fontSize = TrayMenuWindow.ControlCenterButtonFontSize,
        bool compactLabels = false, int columns = 0, bool fitToContent = false)
    {
        var labels = options?.ToArray() ?? ["智能模式", "安静模式", "性能模式", "创作模式"];
        if (columns == 0) columns = labels.Length;
        if (columns < 1 || columns > labels.Length) throw new ArgumentOutOfRangeException(nameof(columns));
        _ = OptionSetPolicy.RemapConfirmed(labels, 0, labels);
#if LIQUID_GLASS
        _glass = new(window, acceleration: true, labels, groupName, fontSize, compactLabels, columns, fitToContent);
        _glass.SelectionRequested += index =>
        {
            if (SelectionRequested is { } requested) requested(index);
            else _glass.SelectIndex(index);
        };
        _glass.SelectionChanged += index => SelectionChanged?.Invoke(index);
#else
        _labels = labels;
        _selection = new(labels.Length);
#endif
    }

    public void SelectIndex(int index, bool animate = false, bool forceSnap = false)
    {
#if LIQUID_GLASS
        _glass.SelectIndex(index, animate, forceSnap);
#else
        index = Math.Clamp(index, 0, OptionCount - 1);
        bool changed = index != SelectedIndex;
        _selection.Confirm(index);
        if (changed) SelectionChanged?.Invoke(index);
#endif
    }

    internal void RequestSelection(int index, bool animate = true, bool forceSnap = false)
    {
#if LIQUID_GLASS
        _glass.RequestSelection(index, animate, forceSnap);
#else
        if (_selection.IsBusy || !_selection.IsEnabled(index)) return;
        if (SelectionRequested is null || index == SelectedIndex)
        {
            SelectIndex(index);
            return;
        }
        if (_selection.Request(index)) SelectionRequested.Invoke(index);
#endif
    }

    public void SetOptionEnabled(int index, bool enabled)
    {
#if LIQUID_GLASS
        _glass.SetOptionEnabled(index, enabled);
#else
        _selection.SetEnabled(index, enabled);
#endif
    }

    public void UpdateOptions(string[] labels, int columns)
    {
        if (columns < 1 || columns > labels.Length) throw new ArgumentOutOfRangeException(nameof(columns));
#if LIQUID_GLASS
        _glass.UpdateOptions(labels, columns);
#else
        int selected = OptionSetPolicy.RemapConfirmed(_labels, SelectedIndex, labels);
        var enabled = _labels.Select((label, index) => (label, value: _selection.IsEnabled(index)))
            .ToDictionary(item => item.label, item => item.value, StringComparer.Ordinal);
        _labels = labels.ToArray();
        _selection = new(labels.Length);
        for (int i = 0; i < labels.Length; i++)
            _selection.SetEnabled(i, !enabled.TryGetValue(labels[i], out bool value) || value);
        _selection.Confirm(selected);
#endif
    }

    internal void SetReveal(double progress)
    {
#if LIQUID_GLASS
        _glass.SetReveal(progress);
#endif
    }

    internal void SetNaturalWidths(double[] widths)
    {
#if LIQUID_GLASS
        _glass.SetNaturalWidths(widths);
#endif
    }

    public void CancelPreview()
    {
#if LIQUID_GLASS
        _glass.CancelPreview();
#endif
    }

    public void Dispose()
    {
#if LIQUID_GLASS
        _glass.Dispose();
#endif
    }
}
