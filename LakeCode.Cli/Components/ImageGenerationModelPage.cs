using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Settings;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Image selection is primary; carrier is an explicit compact choice.</summary>
internal sealed class ImageGenerationModelPage
{
    private readonly ProviderRegistry _providers;
    private readonly ImageGenerationSelection? _initial;
    private readonly ListControl _images = Choices(7);
    private readonly DropdownControl _carrier = Dropdown("Чат-модель-посредник: ");
    private readonly List<string?> _carrierIds = [];
    private readonly MarkupControl _status = Label("");
    private readonly Dictionary<string, string> _carrierDrafts = new(StringComparer.Ordinal);
    private string? _providerId;
    private string _imageId = "";
    private bool _updating;
    public PanelControl Panel { get; } = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
        BackgroundColor = Surface
    };
    // The existing editor remains available for manual IDs and connections without discovery.
    public ChatModelPicker Carrier { get; }
    private readonly PromptControl _manualImage;
    public bool DropdownOpen => _carrier.IsDropdownOpen || Carrier.DropdownOpen;

    public ImageGenerationModelPage(ProviderRegistry providers, ImageGenerationSelection? initial)
    {
        _providers = providers;
        _initial = initial;
        _providerId = initial?.ProviderId;
        _imageId = initial?.ImageModelId ?? "";
        if (initial is not null) _carrierDrafts[initial.ProviderId] = initial.CarrierModelId;
        Carrier = new ChatModelPicker(providers, initial?.ProviderId, initial?.CarrierModelId, "Не настроено", responsesOnly: true);
        _manualImage = Field("Image model ID: ", _imageId);
        _manualImage.MaxLength = 512;
        var advanced = new CollapsiblePanel
        {
            Title = "Обновление списка и ручные ID", IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Margin(2, 0, 2, 0), MaxContentHeight = 12
        };
        Carrier.Panel.AddControl(_manualImage);
        var useManual = Button("Выбрать ручное назначение");
        Carrier.Panel.AddControl(useManual);
        advanced.AddControl(Carrier.Panel);
        Panel.AddControl(Label("Модель генерации изображений"));
        Panel.AddControl(_images);
        Panel.AddControl(_carrier);
        Panel.AddControl(_status);
        Panel.AddControl(advanced);
        Panel.AddControl(Label("[grey70]Платная генерация. Если сервер подменит image-модель, результат сохранится с предупреждением.[/]"));
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
        };
        _carrier.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _providerId is null) return;
            var index = _carrier.SelectedIndex;
            if (index > 0 && index < _carrierIds.Count) _carrierDrafts[_providerId] = _carrierIds[index]!;
            else _carrierDrafts.Remove(_providerId);
            UpdateStatus();
        };
        useManual.Click += (_, _) =>
        {
            try
            {
                var id = Carrier.ProviderId ?? throw new InvalidDataException("Выберите подключение в ручных настройках.");
                var selection = new ImageGenerationSelection(id, Carrier.ModelId, _manualImage.Input.Trim());
                selection.Validate();
                selection.Resolve(_providers);
                _providerId = id;
                _imageId = selection.ImageModelId;
                _carrierDrafts[id] = selection.CarrierModelId;
                advanced.IsExpanded = false;
                Refresh();
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            { _status.Text = Markup.Escape(error.Message); }
        };
        Refresh();
    }

    public void Refresh()
    {
        Carrier.Refresh();
        _updating = true;
        try
        {
            _images.ClearItems();
            _images.AddItem(new ListItem("Не настроено"));
            foreach (var provider in _providers.Profiles.Where(p => p.Kind == ProviderKind.OpenAI && p.ApiMode == OpenAIApiMode.Responses)
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            foreach (var id in (provider.Discovery?.Ids ?? []).Concat(provider.ModelOverrides.Keys)
                         .Distinct(StringComparer.Ordinal).Where(id => ModelCatalog.GetModelType(id) == ModelType.ImageGeneration)
                         .OrderBy(id => id, StringComparer.Ordinal))
                _images.AddItem(new ListItem(Markup.Escape($"{provider.Name} · {id}")) { Tag = (provider.Id, id) });
            var selected = _images.Items.FindIndex(item => item.Tag is ValueTuple<string, string> pair && pair.Item1 == _providerId && pair.Item2 == _imageId);
            if (_providerId is not null && selected < 0 && ModelCatalog.GetModelType(_imageId) == ModelType.ImageGeneration)
            {
                var name = _providers.Profiles.FirstOrDefault(p => p.Id == _providerId)?.Name ?? "Подключение недоступно";
                // Preserve explicit assignments without silently replacing them with the first discovered model.
                _images.AddItem(new ListItem(Markup.Escape($"{name} · {_imageId} · вне списка / вручную")) { Tag = (_providerId, _imageId) });
                selected = _images.Items.Count - 1;
            }
            _images.SelectedIndex = selected < 0 && _providerId is null ? 0 : selected;
        }
        finally { _updating = false; }
        RefreshCarrier();
    }

    private void RefreshCarrier()
    {
        _updating = true;
        try
        {
            _carrier.ClearItems();
            _carrierIds.Clear();
            _carrier.AddItem("Выберите посредника");
            _carrierIds.Add(null);
            var profile = _providers.Profiles.FirstOrDefault(p => p.Id == _providerId);
            foreach (var model in (profile?.Models ?? []).OrderBy(m => m.Id, StringComparer.Ordinal))
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
        _status.Text = Markup.Escape(_providerId is null
            ? _images.Items.Count == 1 ? "Нет image-моделей. Откройте «Обновление списка и ручные ID» и загрузите список провайдера." : "Выберите image-модель из списка."
            : !_carrierDrafts.ContainsKey(_providerId) ? "Для Responses явно выберите чат-модель-посредник ниже списка."
            : $"Image: {_imageId} · carrier: {_carrierDrafts[_providerId]}");
    }

    public ImageGenerationSelection? Capture(ProviderRegistry providers)
    {
        if (_providerId is null) return null;
        var carrier = _carrierDrafts.GetValueOrDefault(_providerId);
        if (string.IsNullOrEmpty(carrier)) throw new InvalidDataException("Выберите чат-модель-посредник для генерации изображений.");
        var selection = new ImageGenerationSelection(_providerId, carrier, _imageId);
        selection.Validate();
        if (selection != _initial) selection.Resolve(providers);
        return selection;
    }
}
