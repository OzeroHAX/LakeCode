using System.Collections.Immutable;
using System.Globalization;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Raw per-model drafts survive navigation even when incomplete or invalid.</summary>
internal sealed class ProviderModelEditor
{
    internal sealed record Draft(int Mode, string CatalogId, bool Manual, string Name, string Context,
        string Input, string Output, int InputModalities, int OutputModalities, string Reasoning)
    {
        public static Draft FromRule(ModelOverride? rule)
        {
            var values = rule?.Definition ?? rule?.Overrides;
            return new(rule?.Definition is not null ? 2 : rule?.CatalogModelId is not null ? 1 : 0,
                rule?.CatalogModelId ?? "", rule?.Manual ?? false, values?.Name ?? "",
                values?.ContextWindowTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                values?.MaxInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                values?.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                ModalityIndex(values?.InputModalities), ModalityIndex(values?.OutputModalities),
                values?.ReasoningEfforts is { } efforts ? string.Join(',', efforts) : "");
        }

        private static int ModalityIndex(ImmutableArray<ModelModality>? values) => values is null ? 0
            : values.Value.Contains(ModelModality.Text) ? values.Value.Contains(ModelModality.Image) ? 3 : 1 : 2;

        public ModelOverride? ToRule()
        {
            var capabilities = new ModelCapabilities
            {
                Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
                ContextWindowTokens = Number(Context, "Контекст"), MaxInputTokens = Number(Input, "Макс. вход"),
                MaxOutputTokens = Number(Output, "Макс. выход"), InputModalities = Modalities(InputModalities),
                OutputModalities = Modalities(OutputModalities), ReasoningEfforts = string.IsNullOrWhiteSpace(Reasoning) ? null
                    : Reasoning.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray()
            };
            var empty = capabilities == new ModelCapabilities();
            if (Mode == 0 && !Manual && empty) return null;
            if (Mode == 1 && string.IsNullOrWhiteSpace(CatalogId)) throw new InvalidDataException("Выберите модель каталога.");
            return new ModelOverride
            {
                Manual = Manual, CatalogModelId = Mode == 1 ? CatalogId : null,
                Definition = Mode == 2 ? capabilities : null, Overrides = Mode == 2 || empty ? null : capabilities
            };
        }

        private static int? Number(string text, string name) => string.IsNullOrWhiteSpace(text) ? null
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number : throw new InvalidDataException($"{name}: требуется положительное целое число.");

        private static ImmutableArray<ModelModality>? Modalities(int index) => index switch
        {
            0 => null, 1 => [ModelModality.Text], 2 => [ModelModality.Image],
            _ => [ModelModality.Text, ModelModality.Image]
        };
    }

    public PanelControl Control { get; } = new()
    { HorizontalAlignment = HorizontalAlignment.Stretch, BackgroundColor = Surface };
    private readonly DropdownControl _mode = Dropdown("Описание: ", "Автоматически", "Из нашего каталога", "Полное описание");
    private readonly DropdownControl _catalog = Dropdown("Модель каталога: ");
    private readonly CheckboxControl _manual = new("Разрешить без discovery (ручной API ID)", false)
    { BackgroundColor = Surface, ForegroundColor = SharpConsoleUI.Color.White, FocusedBackgroundColor = Highlight, Margin = new Margin(2, 1, 2, 0) };
    private readonly PromptControl _name = Field("Название: ");
    private readonly PromptControl _context = Field("Контекст: ");
    private readonly PromptControl _input = Field("Макс. вход: ");
    private readonly PromptControl _output = Field("Макс. выход: ");
    private readonly DropdownControl _inputModalities = Dropdown("Вход: ", "Из каталога", "Text", "Image", "Text, Image");
    private readonly DropdownControl _outputModalities = Dropdown("Выход: ", "Из каталога", "Text", "Image", "Text, Image");
    private readonly PromptControl _reasoning = Field("Reasoning: ");
    private readonly MarkupControl _hint = Label("");
    private bool _loading;

    public ProviderModelEditor()
    {
        foreach (var model in ModelCatalog.Models.Values.OrderBy(model => model.Id, StringComparer.Ordinal)) _catalog.AddItem(model.Id);
        Control.AddControl(_mode);
        Control.AddControl(_catalog);
        Control.AddControl(_manual);
        Control.AddControl(_hint);
        Control.AddControl(_name);
        Control.AddControl(_context);
        Control.AddControl(_input);
        Control.AddControl(_output);
        Control.AddControl(_inputModalities);
        Control.AddControl(_outputModalities);
        Control.AddControl(_reasoning);
        _mode.SelectedIndexChanged += (_, _) => { if (!_loading) UpdateHints(); };
        _catalog.SelectedIndexChanged += (_, _) => { if (!_loading) UpdateHints(); };
        Control.Visible = false;
    }

    public bool DropdownOpen => _mode.IsDropdownOpen || _catalog.IsDropdownOpen || _inputModalities.IsDropdownOpen || _outputModalities.IsDropdownOpen;

    public Draft Capture() => new(_mode.SelectedIndex, _catalog.SelectedValue ?? "", _manual.Checked,
        _name.Input, _context.Input, _input.Input, _output.Input, _inputModalities.SelectedIndex,
        _outputModalities.SelectedIndex, _reasoning.Input);

    private string _apiId = "";
    public void Load(string apiId, Draft draft)
    {
        _loading = true;
        _apiId = apiId;
        _mode.SelectedIndex = draft.Mode;
        var index = _catalog.StringItems.IndexOf(draft.CatalogId);
        // Preserve missing references rather than silently replacing them with the first catalog model.
        if (index < 0 && draft.CatalogId.Length > 0) { _catalog.AddItem(draft.CatalogId); index = _catalog.Items.Count - 1; }
        _catalog.SelectedIndex = index;
        _manual.Checked = draft.Manual;
        _name.Input = draft.Name;
        _context.Input = draft.Context;
        _input.Input = draft.Input;
        _output.Input = draft.Output;
        _inputModalities.SelectedIndex = draft.InputModalities;
        _outputModalities.SelectedIndex = draft.OutputModalities;
        _reasoning.Input = draft.Reasoning;
        _loading = false;
        Control.Visible = true;
        UpdateHints();
    }

    private void UpdateHints()
    {
        _catalog.Visible = _mode.SelectedIndex == 1;
        ModelCatalog.Models.TryGetValue(_mode.SelectedIndex == 1 ? _catalog.SelectedValue ?? "" : _apiId, out var basis);
        if (_mode.SelectedIndex == 2) basis = null;
        _name.Placeholder = basis?.Name ?? "Обязательно для полного описания";
        _context.Placeholder = basis?.ContextWindowTokens.ToString(CultureInfo.InvariantCulture) ?? "Обязательно для полного описания";
        _input.Placeholder = basis?.MaxInputTokens.ToString(CultureInfo.InvariantCulture) ?? "Обязательно для полного описания";
        _output.Placeholder = basis?.MaxOutputTokens.ToString(CultureInfo.InvariantCulture) ?? "Обязательно для полного описания";
        _reasoning.Placeholder = basis is null ? "Через запятую: none,low,… или none,budget:N" : string.Join(',', basis.ReasoningEfforts);
        _hint.Text = _mode.SelectedIndex == 2
            ? "[grey70]Заполните все характеристики. Вход/выход нельзя оставлять «Из каталога».[/]"
            : "[grey70]Пустые поля наследуются из каталога. Заданные значения заменяют исходные.\nReasoning — через запятую; массивы заменяются целиком.[/]";
    }
}
