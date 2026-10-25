using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace YogaITS.Tray.UI;

internal sealed record HardwareChoice(string Id, string Label, Func<Task> Apply, bool Enabled = true);

// The two presentations share real confirmed state and one asynchronous request.
// A missing readback stays unselected; this adapter never invents a fallback SET.
internal sealed class HardwareChoiceGroup : IDisposable
{
    private readonly Window _window;
    private readonly string _title;
    private readonly Action _refresh;
    private readonly Action<Exception> _reportError;
    private readonly double _height, _fontSize;
    private readonly bool _compact;
    private readonly Grid _native = new() { Margin = new Thickness(5, 0, 5, 0) };
    private readonly Dictionary<string, (Border Host, Button Button)> _cards = new(StringComparer.Ordinal);
    private HardwareChoice[] _choices = [];
    private string[] _barIds = [], _barLabels = [];
    private ChoiceBar? _bar;
    private OptionGroupPresentation? _presentation;
    private string? _confirmed, _pending, _domain;
    private int _revision, _columns;
    private bool _available, _visible = true, _disposed;
    public AnimatedOptionHost View { get; }
    internal string? ConfirmedId => _confirmed;
    internal string? PendingId => _pending;
    public event Action? PresentationChanged;
    public void RefreshPresentation() => _presentation?.Refresh();

    public HardwareChoiceGroup(Window window, string title, Action refresh, Action<Exception> reportError,
        double height = TrayMenuWindow.OptionTileHeightDip, double fontSize = TrayMenuWindow.ControlCenterButtonFontSize,
        bool compact = false)
    {
        _window = window;
        _title = title;
        _refresh = refresh;
        _reportError = reportError;
        _height = height;
        _fontSize = fontSize;
        _compact = compact;
        View = new(() => WindowMaterial.EffectsEnabled(window), progress =>
        {
            _bar?.SetReveal(progress);
            WindowMaterial.SetReveal(_native, progress);
        });
        RebuildPresentation(null, [], [], 1);
    }

    public void Update(IEnumerable<HardwareChoice> choices, string? confirmed, bool available,
        int columns, bool visible = true, string? domain = null)
    {
        if (_disposed) return;
        var next = choices.ToArray();
        if (next.Length > 16 || next.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != next.Length ||
            next.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() != next.Length)
            throw new ArgumentException("Each hardware choice needs a unique ID and label.", nameof(choices));
        columns = next.Length == 0 ? 1 : Math.Clamp(columns, 1, next.Length);
        bool domainChanged = domain != _domain;
        bool changed = domainChanged || !_choices.Select(item => (item.Id, item.Label))
            .SequenceEqual(next.Select(item => (item.Id, item.Label)));
        if (changed || _visible && !visible)
        {
            ++_revision;
            _pending = null;
        }
        bool confirmedChanged = _confirmed != confirmed;
        _choices = next;
        _available = available;
        _domain = domain;
        _confirmed = next.Any(item => item.Id == confirmed) ? confirmed : null;
        _columns = columns;
        _visible = visible && next.Length != 0;

        string[] ids = next.Select(item => item.Id).ToArray();
        string[] labels = next.Select(item => item.Label).ToArray();
        int selected = Array.IndexOf(ids, _confirmed);
        bool canRemap = _bar is not null && selected >= 0 &&
            Array.IndexOf(_barIds, _confirmed) is var old && old >= 0 &&
            labels.Contains(_barLabels[old], StringComparer.Ordinal) && _barLabels[old] == labels[selected];
        if (selected < 0)
        {
            if (_bar is not null) RebuildPresentation(null, [], [], columns);
        }
        else if (_bar is null || domainChanged || changed && !canRemap)
        {
            RebuildPresentation(selected, ids, labels, columns);
        }
        else
        {
            // Confirm a surviving ID in the old set before removing/reordering.
            if (changed)
            {
                _bar!.SelectIndex(Array.IndexOf(_barIds, _confirmed), animate: true);
                _bar.UpdateOptions(labels, columns);
                _barIds = ids;
                _barLabels = labels;
            }
            if (_pending is null || confirmedChanged) _bar!.SelectIndex(selected, animate: true);
        }
        RefreshNative();
        if (_bar is { } bar)
            for (int i = 0; i < next.Length; i++) bar.SetOptionEnabled(i, available && next[i].Enabled);
        View.SetExpanded(_visible);
    }

    private void RebuildPresentation(int? selected, string[] ids, string[] labels, int columns)
    {
        // Parent can still be null before Loaded; detach from the owned
        // collection directly when capability/readback replaces a presentation.
        _presentation?.Children.Remove(_native);
        _presentation?.Dispose();
        _bar = null;
        _barIds = ids;
        _barLabels = labels;
        if (selected is { } actual)
        {
            var bar = new ChoiceBar(_window, options: labels, groupName: _title, columns: columns,
                fontSize: _fontSize, compactLabels: _compact);
            _bar = bar;
            bar.SelectIndex(actual);
            bar.SelectionRequested += async index =>
            {
                if (ReferenceEquals(_bar, bar) && index >= 0 && index < _barIds.Length)
                    await RequestAsync(_barIds[index]);
            };
        }
        _presentation = new(_bar?.View, _native,
            () => _bar is not null && WindowMaterial.EffectsEnabled(_window), () => _bar?.CancelPreview());
        _presentation.PresentationChanged += () => { View.RefreshMotion(); PresentationChanged?.Invoke(); };
        View.SetChild(_presentation);
    }

    private void RefreshNative()
    {
        foreach (string id in _cards.Keys.Except(_choices.Select(item => item.Id), StringComparer.Ordinal).ToArray())
        {
            _native.Children.Remove(_cards[id].Host);
            _cards.Remove(id);
        }
        while (_native.ColumnDefinitions.Count > _columns) _native.ColumnDefinitions.RemoveAt(_native.ColumnDefinitions.Count - 1);
        while (_native.ColumnDefinitions.Count < _columns) _native.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        int rows = (_choices.Length + _columns - 1) / _columns;
        while (_native.RowDefinitions.Count > rows) _native.RowDefinitions.RemoveAt(_native.RowDefinitions.Count - 1);
        while (_native.RowDefinitions.Count < rows) _native.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (int i = 0; i < _choices.Length; i++)
        {
            var choice = _choices[i];
            if (!_cards.TryGetValue(choice.Id, out var card))
            {
                string id = choice.Id;
                var button = TrayMenuWindow.CreateOptionTile(choice.Label, false, false,
                    _height, _fontSize);
                button.Click += async (_, _) =>
                {
                    int index = Array.FindIndex(_choices, item => item.Id == id);
                    if (index < 0) return;
                    if (_bar is not null) _bar.RequestSelection(index);
                    else await RequestAsync(id);
                };
                card = (new Border { Margin = new Thickness(3), MinHeight = _height, Child = button }, button);
                _cards.Add(id, card);
                _native.Children.Add(card.Item1);
            }
            TrayMenuWindow.SetOptionText(card.Button, choice.Label);
            AutomationProperties.SetName(card.Button, UiAccessibility.GroupedName(_title, choice.Label));
            card.Button.IsEnabled = _available && choice.Enabled;
            TrayMenuWindow.RefreshOptionTile(card.Button, choice.Id == _confirmed, choice.Id == _pending);
            Grid.SetRow(card.Host, i / _columns);
            Grid.SetColumn(card.Host, i % _columns);
            Grid.SetColumnSpan(card.Host, i == _choices.Length - 1 && _choices.Length % _columns == 1 ? _columns : 1);
        }
    }

    internal async Task RequestAsync(string id)
    {
        var choice = _choices.FirstOrDefault(item => item.Id == id);
        if (_disposed || !_visible || !_available || choice is null || !choice.Enabled || _pending is not null || id == _confirmed)
            return;
        int revision = _revision;
        _pending = id;
        RefreshNative();
        Exception? error = null;
        try { await choice.Apply(); }
        catch (Exception ex) { error = ex; }
        finally
        {
            if (!_disposed && revision == _revision)
            {
                _pending = null;
                _refresh(); // Re-read shared service snapshots, never the requested index.
                if (error is not null) _reportError(error);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_revision;
        _pending = null;
        View.Dispose();
        _presentation?.Dispose();
        _cards.Clear();
    }
}
