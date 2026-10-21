using LakeCode.Cli.State;
using LakeCode.Core.Providers;
using LakeCode.Core.Settings;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;

namespace LakeCode.Cli.Components;

internal sealed class ModelsWindowComponent(ModelStore store, AgentSettingsStorage settingsStorage, CancellationToken shutdown)
{
    private static readonly Color Surface = new(30, 36, 39);
    private static readonly Color Highlight = new(35, 65, 75);

    public void Show(ConsoleWindowSystem windowSystem)
    {
        var initialMain = string.IsNullOrEmpty(store.SelectedProviderId) ? null
            : new ModelSelection(store.SelectedProviderId, store.SelectedModel.Id, store.SelectedReasoningEffort);
        var main = CreatePage(initialMain, "Выберите подключение");
        var planning = CreatePage(store.PlanModel, "Как у основного агента");
        var children = CreatePage(store.SubagentModel, "Как у основного агента");
        var compaction = CreatePage(store.CompactionModel, "Не назначена");
        compaction.Picker.Panel.AddControl(Label("[yellow]/compact отправляет историю выбранному подключению. Платный запрос; запускается только вручную.[/]"));
        var pages = new[] { main, planning, children, compaction };
        var classifier = new ClassifierModelPage(store.Providers, store.Settings.Classifier);
        var images = new ImageGenerationModelPage(store.Providers, store.ImageGeneration);
        var tabs = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            BackgroundColor = Surface, ForegroundColor = Color.White,
            ActiveFocusedBackgroundColor = Highlight, ActiveUnfocusedBackgroundColor = Highlight,
            ActiveFocusedForegroundColor = Color.White, ActiveUnfocusedForegroundColor = Color.White
        };
        tabs.AddTab("Основной агент", main.Picker.Panel, false);
        tabs.AddTab("Планирование", planning.Picker.Panel, false);
        tabs.AddTab("Сабагенты", children.Picker.Panel, false);
        tabs.AddTab("Сжатие контекста", compaction.Picker.Panel, false);
        tabs.AddTab("Классификатор", classifier.Panel, false);
        tabs.AddTab("Изображения", images.Panel, false);
        var status = Label("");
        var cancel = Button("Отмена");
        var apply = Button("Применить");
        var buttons = HorizontalGridControl.ButtonRow([cancel, apply]);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Margin(2, 1, 2, 0);
        var busy = false;
        var closed = false;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var token = lifetime.Token;
        var window = new WindowBuilder(windowSystem)
            .WithTitle("Выбор модели").WithSize(88, 28)
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(Surface).WithForegroundColor(Color.White)
            .AddControl(tabs).AddControl(status).AddControl(buttons)
            .AddControl(Label("[grey70]Tab — переход · ← → — вкладки · ↑ ↓ — модель · Esc — отмена[/]"))
            .OnClosed((_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); })
            .Build();
        foreach (var picker in pages.Select(p => p.Picker).Append(images.Carrier))
        {
            picker.RefreshRequested += id => _ = ProviderActionAsync(
                ct => store.Providers.RefreshDiscoveryAsync(id, ct), "Получение списка моделей…", "Список обновлён и сохранён. Назначения не изменены.");
            picker.ManualRequested += (id, modelId) => _ = ProviderActionAsync(
                ct => store.Providers.AllowManualModelAsync(id, modelId, ct), "Сохранение ручного ID…", "Ручной ID разрешён в подключении. Назначения применяются отдельно.");
        }
        cancel.Click += (_, _) => { if (!busy) windowSystem.CloseWindow(window); };
        apply.Click += (_, _) => _ = ApplyAsync();
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.Escape) return;
            if (pages.Any(p => p.Effort.IsDropdownOpen || p.Picker.DropdownOpen) || images.DropdownOpen || classifier.DropdownOpen) return;
            e.Handled = true;
            if (!busy) windowSystem.CloseWindow(window);
        };
        windowSystem.AddWindow(window);
        window.FocusControl(tabs);

        void Busy(bool value)
        {
            busy = value;
            tabs.IsEnabled = cancel.IsEnabled = apply.IsEnabled = !value;
        }

        async Task ProviderActionAsync(Func<CancellationToken, Task> action, string running, string success)
        {
            if (busy || closed) return;
            Busy(true);
            status.Text = Markup.Escape(running);
            try
            {
                await Task.Run(() => action(token), token).ConfigureAwait(false);
                windowSystem.EnqueueOnUIThread(() =>
                {
                    // Refresh does not replace a missing current assignment with another model.
                    store.Refresh();
                    if (closed) return;
                    foreach (var page in pages) page.Refresh();
                    images.Refresh();
                    Busy(false);
                    status.Text = Markup.Escape(success);
                });
            }
            catch (Exception error)
            {
                windowSystem.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    Busy(false);
                    ShowError(error);
                });
            }
        }

        async Task ApplyAsync()
        {
            if (busy) return;
            ModelSelection? mainSelection;
            AgentSettings settings;
            try
            {
                mainSelection = main.Selection;
                // An unchanged unavailable assignment must not prevent editing another role.
                if (mainSelection != initialMain) mainSelection?.Resolve(store.Providers, "основного агента");
                if (planning.Selection != store.PlanModel) planning.Selection?.Resolve(store.Providers, "планирования");
                if (children.Selection != store.SubagentModel) children.Selection?.Resolve(store.Providers);
                if (compaction.Selection != store.CompactionModel) compaction.Selection?.Resolve(store.Providers, "сжатия контекста");
                settings = store.Settings with
                {
                    PlanModel = planning.Selection, SubagentModel = children.Selection, CompactionModel = compaction.Selection,
                    Classifier = classifier.Capture(store.Providers), ImageGeneration = images.Capture(store.Providers)
                };
            }
            catch (Exception error) { ShowError(error); return; }
            Busy(true);
            status.Text = "[cyan]Применение…[/]";
            try
            {
                if (settings.SubagentModel != store.SubagentModel || settings.PlanModel != store.PlanModel || settings.CompactionModel != store.CompactionModel || settings.Classifier != store.Settings.Classifier || settings.ImageGeneration != store.ImageGeneration)
                    await Task.Run(() => settingsStorage.SaveAsync(settings, token), token).ConfigureAwait(false);
                windowSystem.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    if (mainSelection is not null && mainSelection != initialMain)
                    {
                        store.SelectModel(mainSelection.ProviderId, mainSelection.ModelId);
                        store.SelectReasoningEffort(mainSelection.ReasoningEffort);
                    }
                    store.ApplySettings(settings);
                    windowSystem.CloseWindow(window);
                });
            }
            catch (Exception error)
            {
                windowSystem.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    Busy(false);
                    ShowError(error);
                });
            }
        }

        void ShowError(Exception error) => status.Text = $"[yellow]{Markup.Escape(error is OperationCanceledException ? "Операция отменена." : error.Message)}[/]";
    }

    private sealed class ModelPage(ChatModelPicker picker, DropdownControl effort)
    {
        public ChatModelPicker Picker { get; } = picker;
        public DropdownControl Effort { get; } = effort;
        public ModelSelection? Selection;
        public Action Refresh { get; set; } = () => { };
    }

    private ModelPage CreatePage(ModelSelection? initial, string emptyLabel)
    {
        var picker = new ChatModelPicker(store.Providers, initial?.ProviderId, initial?.ModelId, emptyLabel);
        var effort = ProviderWindowControls.Dropdown("Reasoning: ");
        picker.Panel.AddControl(effort);
        var page = new ModelPage(picker, effort) { Selection = initial };
        var updating = false;
        picker.Changed += () => UpdateSelection(preserveEffort: false);
        effort.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && page.Selection is { } selection && effort.SelectedValue is { } value)
                page.Selection = selection with { ReasoningEffort = value };
        };
        page.Refresh = () => { picker.Refresh(); UpdateSelection(preserveEffort: true); };
        UpdateSelection(preserveEffort: true);
        return page;

        void UpdateSelection(bool preserveEffort)
        {
            updating = true;
            try
            {
                effort.ClearItems();
                if (picker.ProviderId is not { } providerId)
                {
                    page.Selection = null;
                    effort.AddItem(emptyLabel);
                    effort.SelectedIndex = 0;
                    effort.IsEnabled = false;
                    return;
                }
                var previousEffort = page.Selection?.ReasoningEffort ?? store.SelectedReasoningEffort;
                var provider = store.Providers.Profiles.FirstOrDefault(p => p.Id == providerId);
                var model = provider is null || !ProviderProfile.ValidModelId(picker.ModelId) ? null : ModelResolver.Resolve(provider, picker.ModelId).Model;
                if (model is null)
                {
                    page.Selection = new ModelSelection(providerId, picker.ModelId, previousEffort);
                    effort.AddItem(previousEffort);
                    effort.SelectedIndex = 0;
                    effort.IsEnabled = false;
                    return;
                }
                foreach (var value in model.ReasoningEfforts) effort.AddItem(value);
                var index = model.ReasoningEfforts.IndexOf(previousEffort);
                if (index < 0 && preserveEffort) { effort.AddItem(previousEffort); index = effort.Items.Count - 1; }
                effort.SelectedIndex = index < 0 ? 0 : index;
                effort.IsEnabled = true;
                page.Selection = new ModelSelection(providerId, picker.ModelId, effort.SelectedValue!);
            }
            finally { updating = false; }
        }
    }

    private static MarkupControl Label(string text) => new([text])
    { BackgroundColor = Surface, Wrap = true, Margin = new Margin(2, 1, 2, 0) };

    private static ButtonControl Button(string text) => new()
    {
        Text = text, BackgroundColor = Surface, ForegroundColor = Color.White,
        FocusedBackgroundColor = Highlight, Margin = new Margin(1, 0, 1, 0)
    };
}
