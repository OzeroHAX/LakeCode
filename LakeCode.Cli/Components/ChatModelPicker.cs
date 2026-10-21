using LakeCode.Core.Providers;
using SharpConsoleUI.Controls;
using Spectre.Console;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Selection is a draft; refreshing discovery never selects a different model.</summary>
internal sealed class ChatModelPicker
{
    private readonly ProviderRegistry _registry;
    private readonly bool _responsesOnly;
    private readonly DropdownControl _providers;
    private readonly List<string?> _providerIds = [];
    private readonly ListControl _models = Choices(4);
    private readonly PromptControl _filter = Field("Поиск ID: ");
    private readonly PromptControl _model = Field("Чат API ID: ");
    private readonly MarkupControl _discovery = Label("");
    private readonly MarkupControl _state = Label("");
    private readonly ButtonControl _refresh = Button("Обновить список провайдера");
    private readonly ButtonControl _manual = Button("Использовать ID вручную");
    private bool _updating;
    public ScrollablePanelControl Panel { get; } = Form();
    public string? ProviderId => _providers.SelectedIndex >= 0 && _providers.SelectedIndex < _providerIds.Count
        ? _providerIds[_providers.SelectedIndex] : null;
    public string ModelId => _model.Input.Trim();
    public bool DropdownOpen => _providers.IsDropdownOpen;
    public event Action? Changed;
    public event Action<string>? RefreshRequested;
    public event Action<string, string>? ManualRequested;

    public ChatModelPicker(ProviderRegistry registry, string? providerId, string? modelId, string emptyLabel,
        bool responsesOnly = false)
    {
        _registry = registry;
        _responsesOnly = responsesOnly;
        _providers = Dropdown("Подключение: ", emptyLabel);
        _providerIds.Add(null);
        foreach (var provider in registry.Profiles.Where(p => p.Kind != ProviderKind.TypeSafe &&
                     (!responsesOnly || p.Kind == ProviderKind.OpenAI && p.ApiMode == OpenAIApiMode.Responses))
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            _providers.AddItem(provider.Name);
            _providerIds.Add(provider.Id);
        }
        if (!string.IsNullOrEmpty(providerId) && !_providerIds.Contains(providerId))
        {
            _providers.AddItem("Подключение недоступно: " + providerId);
            _providerIds.Add(providerId);
        }
        _providers.SelectedIndex = Math.Max(0, _providerIds.IndexOf(providerId));
        _model.MaxLength = 512;
        _model.Input = modelId ?? "";
        Panel.AddControl(_providers);
        Panel.AddControl(_discovery);
        Panel.AddControl(_refresh);
        Panel.AddControl(_filter);
        Panel.AddControl(_models);
        Panel.AddControl(_model);
        Panel.AddControl(_state);
        Panel.AddControl(_manual);
        Panel.AddControl(Label("Обновление списка сохраняется сразу, отдельно от назначения. Ручной ID сохраняется только по кнопке; характеристики должны быть заданы в каталоге или /providers. Пробных генераций нет."));
        _providers.SelectedIndexChanged += (_, _) => { Refresh(); Changed?.Invoke(); };
        _filter.InputChanged += (_, _) => Refresh();
        _model.InputChanged += (_, _) => { UpdateState(); if (!_updating) Changed?.Invoke(); };
        _models.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating && _models.SelectedItem?.Tag is string id)
            {
                _updating = true;
                try { _model.Input = id; }
                finally { _updating = false; }
                UpdateState();
                Changed?.Invoke();
            }
        };
        _refresh.Click += (_, _) => { if (ProviderId is { } id) RefreshRequested?.Invoke(id); };
        _manual.Click += (_, _) => { if (ProviderId is { } id) ManualRequested?.Invoke(id, ModelId); };
        Refresh();
    }

    public void Refresh()
    {
        _updating = true;
        try
        {
            var profile = _registry.Profiles.FirstOrDefault(p => p.Id == ProviderId);
            _models.ClearItems();
            if (profile is not null)
            {
                var ids = (profile.Discovery?.Ids ?? []).Concat(profile.ModelOverrides.Keys)
                    .Concat(string.IsNullOrWhiteSpace(ModelId) ? [] : new[] { ModelId })
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
                foreach (var id in ids.Where(id => id.Contains(_filter.Input.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    var entry = ModelResolver.Resolve(profile, id);
                    if (entry.Type == LakeCode.Core.Agent.ModelType.ImageGeneration && id != ModelId) continue;
                    var state = entry.Error is not null ? "нужны чат-характеристики"
                        : entry.Listing == ModelListingState.Manual ? "вручную"
                        : entry.Listing == ModelListingState.Listed ? "из списка" : "не подтверждён списком";
                    _models.AddItem(new ListItem(Markup.Escape($"{id} · {state}")) { Tag = id });
                }
            }
            if (_models.Items.Count == 0) _models.AddItem(new ListItem("Нет совпадений. Обновите список или введите API ID."));
            _models.SelectedIndex = _models.Items.FindIndex(item => item.Tag as string == ModelId);
            _refresh.IsEnabled = profile is not null;
            _model.IsEnabled = _filter.IsEnabled = ProviderId is not null;
            _discovery.Text = Markup.Escape(profile is null
                ? ProviderId is null ? "Выберите подключение. Автоматической загрузки при запуске нет." : "Подключение удалено или недоступно. Выбор сохранён."
                : DiscoveryText(profile));
            UpdateState();
        }
        finally { _updating = false; }
    }

    private void UpdateState()
    {
        var profile = _registry.Profiles.FirstOrDefault(p => p.Id == ProviderId);
        _manual.IsEnabled = false;
        if (ProviderId is null) { _state.Text = ""; return; }
        if (profile is null) { _state.Text = "[yellow]Подключение недоступно.[/]"; return; }
        if (profile.Kind == ProviderKind.TypeSafe || _responsesOnly && (profile.Kind != ProviderKind.OpenAI || profile.ApiMode != OpenAIApiMode.Responses))
        {
            _state.Text = "[yellow]Протокол подключения несовместим с этой ролью. Выбор сохранён; выберите подходящее подключение.[/]";
            return;
        }
        if (!ProviderProfile.ValidModelId(ModelId)) { _state.Text = "Введите API ID или выберите строку списка."; return; }
        var entry = ModelResolver.Resolve(profile, ModelId);
        if (entry.Type == LakeCode.Core.Agent.ModelType.ImageGeneration)
        {
            _state.Text = "Модель генерации изображений. Выберите её как image model ID, а для carrier укажите чат-модель.";
            return;
        }
        _manual.IsEnabled = entry.Model is not null && entry.Listing != ModelListingState.Manual;
        _state.Text = Markup.Escape(ModelResolver.ListingDescription(entry.Listing) +
            (entry.Error is null ? ". Возможности API сейчас не проверялись." : ". " + entry.Error + " Настройте характеристики в /providers."));
    }

    internal static string DiscoveryText(ProviderProfile profile) => profile.Discovery is { } discovery
        ? $"Последний список: {discovery.UpdatedAt.ToLocalTime():g} · ID: {discovery.Ids.Length}. Это не проверка доступности и возможностей API."
        : "Список ещё не получен. Загрузите его или явно разрешите ручной ID.";
}
