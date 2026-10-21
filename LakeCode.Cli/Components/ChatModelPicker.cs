using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Settings;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>
/// Compact role picker: one filter row, one model list, one reasoning row.
/// Selection is a draft; refreshing discovery never selects a different model.
/// </summary>
internal sealed class ChatModelPicker
{
    private sealed record Row(string? ProviderId, string? ModelId, ImmutableArray<string> Efforts, string Hint, bool Selectable);

    private readonly ProviderRegistry _registry;
    private readonly string? _emptyLabel;
    private readonly DropdownControl _providers;
    private readonly List<string?> _providerIds = [null];
    private readonly PromptControl _search = new()
    {
        Prompt = "поиск: ", Placeholder = "ID или подключение", UnfocusOnEnter = false, HistoryEnabled = false, MaxLength = 128,
        HorizontalAlignment = HorizontalAlignment.Stretch, InputBackgroundColor = Surface,
        InputFocusedBackgroundColor = Highlight, InputForegroundColor = Color.White,
        InputFocusedForegroundColor = Color.White, Margin = new Margin(1, 0, 2, 0)
    };
    private readonly ListControl _rows = new()
    {
        IsSelectable = true, BackgroundColor = Surface,
        FocusedBackgroundColor = Surface, ForegroundColor = Color.White,
        HighlightBackgroundColor = Highlight, HighlightForegroundColor = Color.White,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
        Margin = new Margin(2, 0, 2, 0)
    };
    private readonly ToolbarControl _efforts = new()
    {
        ItemSpacing = 2, Wrap = true, BackgroundColor = Surface, HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Margin(2, 0, 2, 0), Visible = false
    };
    private readonly MarkupControl _hint = new([])
    {
        BackgroundColor = Surface, Wrap = true, Visible = false, Margin = new Margin(2, 0, 2, 0)
    };
    private RadioGroup<string> _effortGroup = new();
    private ImmutableArray<string> _effortsAvailable = [];
    private string? _providerId;
    private string? _modelId;
    private string _effort;
    private bool _updating;

    public GridControl Panel { get; }
    public bool DropdownOpen => _providers.IsDropdownOpen;
    public string? CurrentProviderId => (_rows.SelectedItem?.Tag as Row)?.ProviderId ?? _providerId;
    public ModelSelection? Selection => _providerId is null || _modelId is null || string.IsNullOrEmpty(_effort)
        ? null
        : new ModelSelection(_providerId, _modelId, _effort);
    /// <summary>Raised when Enter is pressed on a selectable row.</summary>
    public event Action? Activated;

    public ChatModelPicker(ProviderRegistry registry, ModelSelection? initial, string? emptyLabel, string defaultEffort,
        string? notice = null)
    {
        _registry = registry;
        _emptyLabel = emptyLabel;
        _providerId = initial?.ProviderId;
        _modelId = initial?.ModelId;
        _effort = initial?.ReasoningEffort ?? defaultEffort;
        _providers = Dropdown("");
        _providers.Margin = new Margin(2, 0, 2, 0);
        _providers.AddItem("Все подключения");
        foreach (var profile in registry.Profiles.Where(profile => profile.Kind != ProviderKind.TypeSafe)
                     .OrderBy(profile => profile.Name, StringComparer.Ordinal))
        {
            _providers.AddItem(profile.Name);
            _providerIds.Add(profile.Id);
        }
        _providers.SelectedIndex = 0;
        var filter = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var providerColumn = new ColumnContainer(filter) { FlexFactor = 0, Width = 24 };
        providerColumn.AddContent(_providers);
        var searchColumn = new ColumnContainer(filter) { FlexFactor = 1 };
        searchColumn.AddContent(_search);
        filter.AddColumn(providerColumn);
        filter.AddColumn(searchColumn);
        // The list owns the remaining height, so the window never leaves dead space below it.
        Panel = new GridControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            BackgroundColor = Surface
        };
        Panel.ColumnDefinitions.Add(GridLength.Star());
        Panel.RowDefinitions.Add(GridLength.Auto());
        Panel.RowDefinitions.Add(GridLength.Star());
        Panel.RowDefinitions.Add(GridLength.Auto());
        Panel.RowDefinitions.Add(GridLength.Auto());
        Panel.Place(filter, 0, 0);
        Panel.Place(_rows, 1, 0);
        Panel.Place(_efforts, 2, 0);
        Panel.Place(_hint, 3, 0);
        if (notice is not null)
        {
            Panel.RowDefinitions.Add(GridLength.Auto());
            Panel.Place(Label(notice), 4, 0);
        }
        _providers.SelectedIndexChanged += (_, _) => { if (!_updating) Refresh(); };
        _search.InputChanged += (_, _) => { if (!_updating) Refresh(); };
        _rows.SelectedIndexChanged += (_, _) => OnRowChanged();
        _rows.ItemActivated += (_, _) => OnActivated();
        Refresh();
    }

    /// <summary>Rebuilds rows from the registry; a missing assignment stays visible instead of being replaced.</summary>
    public void Refresh()
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            _rows.ClearItems();
            if (_emptyLabel is not null)
                AddRow(new Row(null, null, [], "", true), $"[grey70]{Markup.Escape(_emptyLabel)}[/]");
            var filter = FilterProviderId;
            foreach (var profile in _registry.Profiles.Where(profile => profile.Kind != ProviderKind.TypeSafe)
                         .Where(profile => filter is null || profile.Id == filter)
                         .OrderBy(profile => profile.Name, StringComparer.Ordinal))
            {
                var ids = (profile.Discovery?.Ids ?? ImmutableArray<string>.Empty)
                    .Concat(profile.ModelOverrides.Keys)
                    .Concat(profile.Id == _providerId && _modelId is not null ? new[] { _modelId } : [])
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    var draftRow = profile.Id == _providerId && id == _modelId;
                    if (!draftRow && !Matches(profile.Name, id)) continue;
                    var entry = ModelResolver.Resolve(profile, id);
                    if (entry.Type == ModelType.ImageGeneration) continue;
                    var selectable = entry.Available && entry.Error is null && entry.Model is not null;
                    var glyph = selectable ? "[green]●[/]" : entry.Error is not null ? "[red]✕[/]" : "[yellow]?[/]";
                    var hint = entry.Error is not null ? "[grey70]нет характеристик · /providers[/]"
                        : selectable ? "" : "[grey70]вне списка · F2[/]";
                    var label = filter is null ? $"{profile.Name} · {id}" : id;
                    AddRow(new Row(profile.Id, id, entry.Model?.ReasoningEfforts ?? [], hint, selectable),
                        $"{glyph} {Markup.Escape(label)}");
                }
            }
            if (_rows.Items.Count == 0) _rows.AddItem(new ListItem("[grey70]Нет моделей[/]") { IsEnabled = false });
            var index = FindRow();
            _rows.SelectedIndex = index;
            // Only a row that actually carries the draft may refresh it; a clamped selection must not.
            if (index >= 0 && _rows.SelectedItem?.Tag is Row { Selectable: true }) ApplySelectedRow();
            else
            {
                _effortsAvailable = [];
                RebuildEfforts();
                UpdateHint();
            }
        }
        finally { _updating = wasUpdating; }
    }

    /// <summary>Points the draft at a provider/model pair and shows it, e.g. after a manual ID is allowed.</summary>
    public void Select(string providerId, string modelId)
    {
        _providerId = providerId;
        _modelId = modelId;
        var index = _providerIds.IndexOf(providerId);
        _updating = true;
        try { _providers.SelectedIndex = index > 0 ? index : 0; }
        finally { _updating = false; }
        Refresh();
    }

    private string? FilterProviderId => _providers.SelectedIndex >= 0 && _providers.SelectedIndex < _providerIds.Count
        ? _providerIds[_providers.SelectedIndex] : null;

    private void AddRow(Row row, string text) => _rows.AddItem(new ListItem(text) { Tag = row });

    private bool Matches(string providerName, string id)
    {
        var query = _search.Input.Trim();
        return query.Length == 0 ||
            id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            providerName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private int FindRow()
    {
        // The draft row is always listed, so a search or filter never silently replaces the assignment.
        for (var index = 0; index < _rows.Items.Count; index++)
            if (_rows.Items[index].Tag is Row row && row.ProviderId == _providerId && row.ModelId == _modelId)
                return index;
        return -1;
    }

    private void OnRowChanged()
    {
        if (_updating) return;
        ApplySelectedRow();
    }

    private void OnActivated()
    {
        if (_updating) return;
        if (_rows.SelectedItem?.Tag is not Row { Selectable: true })
        {
            UpdateHint();
            return;
        }
        Activated?.Invoke();
    }

    private void ApplySelectedRow()
    {
        // A row without characteristics stays visible but never replaces the draft while browsing.
        if (_rows.SelectedItem?.Tag is Row { Selectable: true } row)
        {
            _providerId = row.ProviderId;
            _modelId = row.ModelId;
            _effortsAvailable = row.Efforts;
        }
        RebuildEfforts();
        UpdateHint();
    }

    private void RebuildEfforts()
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            _efforts.Clear();
            _effortGroup = new RadioGroup<string> { AllowDeselect = false };
            _effortGroup.SelectionChanged += (_, _) =>
            {
                if (_updating || string.IsNullOrEmpty(_effortGroup.SelectedValue)) return;
                _effort = _effortGroup.SelectedValue;
            };
            var efforts = _effortsAvailable;
            _efforts.Visible = !efforts.IsDefaultOrEmpty;
            if (efforts.IsDefaultOrEmpty) return;
            _efforts.AddItem(new MarkupControl(["[grey70]reasoning[/]"]));
            _effort = efforts.Contains(_effort) ? _effort : efforts[0];
            foreach (var value in efforts)
                _efforts.AddItem(new RadioControl<string>(_effortGroup, value, value)
                {
                    BackgroundColor = Surface, ForegroundColor = Color.White,
                    FocusedBackgroundColor = Highlight, FocusedForegroundColor = Color.White
                });
            _effortGroup.SelectedValue = _effort;
        }
        finally { _updating = wasUpdating; }
    }

    private void UpdateHint()
    {
        var row = _rows.SelectedItem?.Tag as Row;
        _hint.Text = row is not null ? row.Hint
            : _modelId is not null ? "[yellow]Назначение недоступно · F2 или /providers[/]"
            : "";
        _hint.Visible = _hint.Text.Length > 0;
    }
}
