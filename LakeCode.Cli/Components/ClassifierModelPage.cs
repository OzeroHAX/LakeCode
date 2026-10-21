using System.Globalization;
using LakeCode.Core.ExecutionApproval;
using LakeCode.Core.Providers;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Role assignment inside /models; connections and credentials are edited only in /providers.</summary>
internal sealed class ClassifierModelPage
{
    private readonly DropdownControl _mode = Dropdown("Режим: ", "Вручную", "Авто");
    private readonly DropdownControl _model = Dropdown("Модель: ");
    private readonly List<ClassifierModelSelection?> _models = [null];
    private readonly PromptControl _timeout;
    private readonly PromptControl _allow;
    private readonly PromptControl _deny;
    public PanelControl Panel { get; } = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, BackgroundColor = Surface, BorderStyle = BorderStyle.None
    };
    public bool DropdownOpen => _mode.IsDropdownOpen || _model.IsDropdownOpen;
    /// <summary>Raised when the draft changes, so the section summary stays current.</summary>
    public event Action? Changed;

    public ClassifierModelPage(ProviderRegistry providers, ClassifierSettings settings)
    {
        _mode.SelectedIndex = settings.Enabled ? 1 : 0;
        _model.AddItem("Не назначена");
        foreach (var provider in providers.Profiles.Where(profile => profile.Kind == ProviderKind.TypeSafe)
                     .OrderBy(profile => profile.Name, StringComparer.Ordinal))
        foreach (var model in provider.ClassificationModels.Where(model => model.IsVersioned))
        {
            _model.AddItem(Markup.Escape($"{provider.Name} · {model.Id}"));
            _models.Add(new ClassifierModelSelection(provider.Id, model.Id));
        }
        var selected = settings.Model is null ? 0 : _models.IndexOf(settings.Model);
        if (settings.Model is not null && selected < 0)
        {
            // Preserve an explicit assignment instead of replacing it with the first available model.
            _model.AddItem(Markup.Escape($"Недоступно: {settings.Model.ProviderId} · {settings.Model.ModelId}"));
            _models.Add(settings.Model);
            selected = _models.Count - 1;
        }
        _model.SelectedIndex = Math.Max(0, selected);
        _timeout = Field("Таймаут, с: ", settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        _allow = Field("Разрешать ≥: ", settings.AllowThreshold.ToString(CultureInfo.InvariantCulture));
        _deny = Field("Запрещать ≥: ", settings.DenyThreshold.ToString(CultureInfo.InvariantCulture));
        Panel.AddControl(_mode);
        Panel.AddControl(_model);
        var thresholds = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var field in new[] { _timeout, _allow, _deny })
        {
            var column = new ColumnContainer(thresholds) { FlexFactor = 1 };
            column.AddContent(field);
            thresholds.AddColumn(column);
        }
        Panel.AddControl(thresholds);
        Panel.AddControl(Label("[grey70]Платно · описание и параметры уходят провайдеру[/]"));
        _mode.SelectedIndexChanged += (_, _) => Changed?.Invoke();
        _model.SelectedIndexChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>One-line draft summary for the collapsed section header.</summary>
    public string Summary
    {
        get
        {
            var mode = _mode.SelectedIndex == 1 ? "авто" : "выкл";
            var index = _model.SelectedIndex;
            var model = index > 0 && index < _models.Count ? _models[index]?.ModelId : null;
            return model is null ? mode : $"{mode} · {model}";
        }
    }

    public ClassifierSettings Capture(ProviderRegistry providers)
    {
        var settings = new ClassifierSettings
        {
            Enabled = _mode.SelectedIndex == 1,
            Model = _model.SelectedIndex > 0 && _model.SelectedIndex < _models.Count ? _models[_model.SelectedIndex] : null,
            TimeoutSeconds = int.Parse(_timeout.Input, CultureInfo.InvariantCulture),
            AllowThreshold = double.Parse(_allow.Input, CultureInfo.InvariantCulture),
            DenyThreshold = double.Parse(_deny.Input, CultureInfo.InvariantCulture)
        };
        settings.Validate();
        if (settings.Enabled) settings.Model!.Resolve(providers);
        return settings;
    }
}
