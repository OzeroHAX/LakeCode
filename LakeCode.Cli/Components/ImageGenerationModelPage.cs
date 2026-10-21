using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Settings;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Image selection is primary; carrier is one explicit choice. Manual IDs live in a separate small dialog.</summary>
internal sealed class ImageGenerationModelPage
{
    private readonly ProviderRegistry _providers;
    private readonly ImageGenerationSelection? _initial;
    private readonly ListControl _images = new()
    {
        IsSelectable = true, MaxVisibleItems = 4, BackgroundColor = Surface, FocusedBackgroundColor = Surface,
        ForegroundColor = Color.White, HighlightBackgroundColor = Highlight, HighlightForegroundColor = Color.White,
        HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Margin(2, 0, 2, 0)
    };
    private readonly DropdownControl _carrier = Dropdown("Посредник: ");
    private readonly List<string?> _carrierIds = [null];
    private readonly MarkupControl _status = Label("");
    private readonly ButtonControl _manual = Button("Ручной ID");
    private readonly Dictionary<string, string> _carrierDrafts = new(StringComparer.Ordinal);
    private string? _providerId;
    private string _imageId = "";
    private bool _updating;
    public PanelControl Panel { get; } = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, BackgroundColor = Surface, BorderStyle = BorderStyle.None
    };
    public bool DropdownOpen => _carrier.IsDropdownOpen;
    public string? ProviderId => _providerId;
    /// <summary>Raised when the manual image ID dialog should open.</summary>
    public event Action? ManualRequested;
    /// <summary>Raised when the draft changes, so the section summary stays current.</summary>
    public event Action? Changed;

    public ImageGenerationModelPage(ProviderRegistry providers, ImageGenerationSelection? initial)
    {
        _providers = providers;
        _initial = initial;
        _providerId = initial?.ProviderId;
        _imageId = initial?.ImageModelId ?? "";
        if (initial is not null) _carrierDrafts[initial.ProviderId] = initial.CarrierModelId;
        Panel.AddControl(_images);
        Panel.AddControl(_carrier);
        Panel.AddControl(_status);
        Panel.AddControl(Buttons(_manual));
        _images.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            if (_images.SelectedItem?.Tag is ValueTuple<string, string> pair)
            {
                _providerId = pair.Item1;
                _imageId = pair.Item2;
            }
            else { _providerId = null; _imageId = ""; }
            RefreshCarrier();
            Changed?.Invoke();
        };
        _carrier.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _providerId is null) return;
            var index = _carrier.SelectedIndex;
            if (index > 0 && index < _carrierIds.Count) _carrierDrafts[_providerId] = _carrierIds[index]!;
            else _carrierDrafts.Remove(_providerId);
            UpdateStatus();
            Changed?.Invoke();
        };
        _manual.Click += (_, _) => ManualRequested?.Invoke();
        Refresh();
    }

    /// <summary>One-line draft summary for the collapsed section header.</summary>
    public string Summary => _providerId is null ? "не настроено"
        : _carrierDrafts.TryGetValue(_providerId, out var carrier) ? $"{_imageId} · через {carrier}"
        : _imageId;

    public void Select(string providerId, string imageId)
    {
        _providerId = providerId;
        _imageId = imageId;
        Refresh();
    }

    public void Refresh()
    {
        _updating = true;
        try
        {
            _images.ClearItems();
            _images.AddItem(new ListItem("Не настроено"));
            foreach (var provider in _providers.Profiles
                         .Where(profile => profile.Kind == ProviderKind.OpenAI && profile.ApiMode == OpenAIApiMode.Responses)
                         .OrderBy(profile => profile.Name, StringComparer.Ordinal))
            foreach (var id in (provider.Discovery?.Ids ?? []).Concat(provider.ModelOverrides.Keys)
                         .Distinct(StringComparer.Ordinal).Where(id => ModelCatalog.GetModelType(id) == ModelType.ImageGeneration)
                         .OrderBy(id => id, StringComparer.Ordinal))
                _images.AddItem(new ListItem(Markup.Escape($"{provider.Name} · {id}")) { Tag = (provider.Id, id) });
            var selected = _images.Items.FindIndex(item =>
                item.Tag is ValueTuple<string, string> pair && pair.Item1 == _providerId && pair.Item2 == _imageId);
            if (_providerId is not null && selected < 0 && ModelCatalog.GetModelType(_imageId) == ModelType.ImageGeneration)
            {
                var name = _providers.Profiles.FirstOrDefault(profile => profile.Id == _providerId)?.Name ?? "вне списка";
                // Preserve explicit assignments without silently replacing them with the first discovered model.
                _images.AddItem(new ListItem(Markup.Escape($"{name} · {_imageId}")) { Tag = (_providerId, _imageId) });
                selected = _images.Items.Count - 1;
            }
            _images.SelectedIndex = selected < 0 && _providerId is null ? 0 : selected;
        }
        finally { _updating = false; }
        RefreshCarrier();
    }

    public ImageGenerationSelection? Capture(ProviderRegistry providers)
    {
        if (_providerId is null) return null;
        var carrier = _carrierDrafts.GetValueOrDefault(_providerId);
        if (string.IsNullOrEmpty(carrier)) throw new InvalidDataException("Выберите посредника для генерации изображений.");
        var selection = new ImageGenerationSelection(_providerId, carrier, _imageId);
        selection.Validate();
        if (selection != _initial) selection.Resolve(providers);
        return selection;
    }

    private void RefreshCarrier()
    {
        _updating = true;
        try
        {
            _carrier.ClearItems();
            _carrierIds.Clear();
            _carrier.AddItem("Не выбран");
            _carrierIds.Add(null);
            var profile = _providers.Profiles.FirstOrDefault(profile => profile.Id == _providerId);
            foreach (var model in (profile?.Models ?? []).OrderBy(model => model.Id, StringComparer.Ordinal))
            {
                _carrier.AddItem(model.Id);
                _carrierIds.Add(model.Id);
            }
            var saved = _providerId is null ? null : _carrierDrafts.GetValueOrDefault(_providerId);
            if (saved is not null && !_carrierIds.Contains(saved))
            {
                _carrier.AddItem(saved + " · недоступно");
                _carrierIds.Add(saved);
            }
            _carrier.SelectedIndex = Math.Max(0, _carrierIds.IndexOf(saved));
            _carrier.IsEnabled = _providerId is not null;
            UpdateStatus();
        }
        finally { _updating = false; }
    }

    private void UpdateStatus()
    {
        _status.Text = _providerId is null
            ? _images.Items.Count == 1 ? "[grey70]нет image-моделей · F5[/]" : "[grey70]не настроено[/]"
            : !_carrierDrafts.ContainsKey(_providerId) ? "[yellow]выберите посредника[/]"
            : "";
        _status.Visible = _status.Text.Length > 0;
    }
}
