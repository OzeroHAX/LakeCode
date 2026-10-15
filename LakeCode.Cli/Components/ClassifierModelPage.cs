using System.Globalization;
using LakeCode.Core.ExecutionApproval;
using LakeCode.Core.Providers;
using SharpConsoleUI.Controls;
using Spectre.Console;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Role assignment inside /models. Connections and credentials are edited only in /providers.</summary>
internal sealed class ClassifierModelPage
{
    public ScrollablePanelControl Panel { get; } = Form();
    private readonly DropdownControl _mode;
    private readonly ListControl _models;
    private readonly PromptControl _timeout;
    private readonly PromptControl _allow;
    private readonly PromptControl _deny;
    public bool DropdownOpen => _mode.IsDropdownOpen;

    public ClassifierModelPage(ProviderRegistry providers, ClassifierSettings settings)
    {
        _mode = Dropdown("Plan: ", "Ручное подтверждение", "Автоматическая оценка");
        _mode.SelectedIndex = settings.Enabled ? 1 : 0;
        _models = Choices(4);
        _models.AddItem(new ListItem("Не назначена"));
        foreach (var provider in providers.Profiles.Where(p => p.Kind == ProviderKind.TypeSafe).OrderBy(p => p.Name, StringComparer.Ordinal))
        foreach (var model in provider.ClassificationModels.Where(m => m.IsVersioned))
            _models.AddItem(new ListItem(Markup.Escape($"{provider.Name} · {model.Id}"))
                { Tag = new ClassifierModelSelection(provider.Id, model.Id) });
        var selected = _models.Items.FindIndex(item => item.Tag is ClassifierModelSelection value && value == settings.Model);
        if (settings.Model is not null && selected < 0)
        {
            _models.AddItem(new ListItem(Markup.Escape($"Недоступно: {settings.Model.ProviderId} · {settings.Model.ModelId}")) { Tag = settings.Model });
            selected = _models.Items.Count - 1;
        }
        _models.SelectedIndex = selected < 0 ? 0 : selected;
        _timeout = Field("Таймаут, секунд: ", settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        _allow = Field("Порог авторазрешения: ", settings.AllowThreshold.ToString(CultureInfo.InvariantCulture));
        _deny = Field("Порог запрета: ", settings.DenyThreshold.ToString(CultureInfo.InvariantCulture));
        Panel.AddControl(_mode);
        Panel.AddControl(Label("Модель классификатора · подключение и фиксированный ID настраиваются в /providers"));
        Panel.AddControl(_models);
        Panel.AddControl(_timeout);
        Panel.AddControl(_allow);
        Panel.AddControl(_deny);
        Panel.AddControl(Label("[yellow]Включение разрешает платную отправку команды/кода, описания и параметров выбранному провайдеру. История и дополнительные файлы не отправляются. Это не sandbox: модель может ошибаться.[/]"));
        Panel.AddControl(Label("Авторазрешение требует safe ≥ порога по всем шести критериям. Запрет: risk ≥ порога для несоответствия описания или передачи непубличных данных. По каждому критерию: нет риска при safe ≥ порога, иначе есть риск. Недостаточная понятность, изменения и финансовые эффекты → ручное решение. Настройки действуют для следующих сообщений."));
    }

    public ClassifierSettings Capture(ProviderRegistry providers)
    {
        var settings = new ClassifierSettings
        {
            Enabled = _mode.SelectedIndex == 1,
            Model = _models.SelectedItem?.Tag as ClassifierModelSelection,
            TimeoutSeconds = int.Parse(_timeout.Input, CultureInfo.InvariantCulture),
            AllowThreshold = double.Parse(_allow.Input, CultureInfo.InvariantCulture),
            DenyThreshold = double.Parse(_deny.Input, CultureInfo.InvariantCulture)
        };
        settings.Validate();
        if (settings.Enabled) settings.Model!.Resolve(providers);
        return settings;
    }
}
