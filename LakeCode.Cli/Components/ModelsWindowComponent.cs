using LakeCode.Cli.State;
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
        var main = CreatePage(false, new ModelSelection(store.SelectedProviderId, store.SelectedModel.Id, store.SelectedReasoningEffort));
        var children = CreatePage(true, store.SubagentModel);
        var tabs = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            BackgroundColor = Surface, ForegroundColor = Color.White,
            ActiveFocusedBackgroundColor = Highlight, ActiveUnfocusedBackgroundColor = Highlight,
            ActiveFocusedForegroundColor = Color.White, ActiveUnfocusedForegroundColor = Color.White
        };
        tabs.AddTab("Основной агент", main.Panel, false);
        tabs.AddTab("Сабагенты", children.Panel, false);
        var status = Label("");
        var cancel = Button("Отмена");
        var apply = Button("Применить");
        var buttons = HorizontalGridControl.ButtonRow([cancel, apply]);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Margin(2, 1, 2, 0);
        var busy = false;
        var window = new WindowBuilder(windowSystem)
            .WithTitle("Выбор модели").WithSize(76, 24)
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(Surface).WithForegroundColor(Color.White)
            .AddControl(tabs).AddControl(status).AddControl(buttons)
            .AddControl(Label("[grey70]Tab — переход · ← → — вкладки · ↑ ↓ — модель · Esc — отмена[/]"))
            .Build();
        cancel.Click += (_, _) => { if (!busy) windowSystem.CloseWindow(window); };
        apply.Click += (_, _) => _ = ApplyAsync();
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.Escape) return;
            // Let an open dropdown consume Esc before closing the dialog.
            if (main.Effort.IsDropdownOpen || children.Effort.IsDropdownOpen) return;
            e.Handled = true;
            if (!busy) windowSystem.CloseWindow(window);
        };
        windowSystem.AddWindow(window);
        window.FocusControl(tabs);

        async Task ApplyAsync()
        {
            if (busy) return;
            ModelSelection mainSelection;
            AgentSettings settings;
            try
            {
                mainSelection = main.Selection ?? throw new InvalidOperationException("Выберите модель основного агента.");
                var model = store.Providers.GetModel(mainSelection.ProviderId, mainSelection.ModelId);
                if (!model.ReasoningEfforts.Contains(mainSelection.ReasoningEffort))
                    throw new InvalidOperationException("Выберите поддерживаемый reasoning основного агента.");
                children.Selection?.Resolve(store.Providers);
                settings = store.Settings with { SubagentModel = children.Selection };
            }
            catch (Exception error) { ShowError(error); return; }
            busy = true;
            tabs.IsEnabled = false;
            cancel.IsEnabled = false;
            apply.IsEnabled = false;
            status.Text = "[cyan]Применение…[/]";
            try
            {
                if (settings.SubagentModel != store.SubagentModel)
                    await Task.Run(() => settingsStorage.SaveAsync(settings, shutdown), shutdown).ConfigureAwait(false);
                windowSystem.EnqueueOnUIThread(() =>
                {
                    store.SelectModel(mainSelection.ProviderId, mainSelection.ModelId);
                    store.SelectReasoningEffort(mainSelection.ReasoningEffort);
                    store.ApplySettings(settings);
                    windowSystem.CloseWindow(window);
                });
            }
            catch (Exception error)
            {
                windowSystem.EnqueueOnUIThread(() =>
                {
                    busy = false;
                    tabs.IsEnabled = cancel.IsEnabled = apply.IsEnabled = true;
                    ShowError(error);
                });
            }
        }

        void ShowError(Exception error) => status.Text = $"[yellow]{Markup.Escape(error.Message)}[/]";
    }

    private sealed class ModelPage(PanelControl panel, DropdownControl effort)
    {
        public PanelControl Panel { get; } = panel;
        public DropdownControl Effort { get; } = effort;
        public ModelSelection? Selection;
    }

    private ModelPage CreatePage(bool subagents, ModelSelection? initial)
    {
        var list = new ListControl
        {
            IsSelectable = true, BackgroundColor = Surface, FocusedBackgroundColor = Surface,
            ForegroundColor = Color.White, HighlightBackgroundColor = Highlight, HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            Margin = new Margin(2, 1, 2, 1)
        };
        if (subagents) list.AddItem(new ListItem("Как у основного агента"));
        foreach (var provider in store.Providers.Profiles.OrderBy(item => item.Name, StringComparer.Ordinal))
        foreach (var model in provider.Models.OrderBy(item => item.Id, StringComparer.Ordinal))
            list.AddItem(new ListItem(Markup.Escape($"{provider.Name} · {model.Name}"))
                { Tag = (provider.Id, model.Id) });
        var selectedIndex = list.Items.FindIndex(item => item.Tag is ValueTuple<string, string> pair &&
            pair.Item1 == initial?.ProviderId && pair.Item2 == initial.ModelId);
        if (subagents && initial is null) selectedIndex = 0;
        if (initial is not null && selectedIndex < 0 && subagents)
        {
            list.AddItem(new ListItem(Markup.Escape($"Недоступно: {initial.ProviderId} · {initial.ModelId}"))
                { Tag = (initial.ProviderId, initial.ModelId) });
            selectedIndex = list.Items.Count - 1;
        }
        if (list.Items.Count == 0) list.AddItem(new ListItem("Нет моделей. Настройте /providers."));
        list.SelectedIndex = selectedIndex;
        var effort = new DropdownControl("Reasoning: ")
        {
            BackgroundColor = Surface, ForegroundColor = Color.White,
            FocusedBackgroundColor = Highlight, FocusedForegroundColor = Color.White,
            HighlightBackgroundColor = Highlight, HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Margin(2, 0, 2, 1)
        };
        var panel = new PanelControl
        {
            BackgroundColor = Surface, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill
        };
        panel.AddControl(Label("Модель"));
        panel.AddControl(list);
        panel.AddControl(effort);
        if (subagents) panel.AddControl(Label("[grey70]Сохраняется для всех проектов.[/]"));
        var page = new ModelPage(panel, effort) { Selection = initial };
        var updating = false;
        list.SelectedIndexChanged += (_, _) => UpdateSelection();
        effort.SelectedIndexChanged += (_, _) =>
        {
            if (!updating && page.Selection is { } selection && effort.SelectedValue is { } value)
                page.Selection = selection with { ReasoningEffort = value };
        };
        UpdateSelection();
        return page;

        void UpdateSelection()
        {
            updating = true;
            try
            {
                effort.ClearItems();
                if (list.SelectedItem?.Tag is not ValueTuple<string, string> pair)
                {
                    page.Selection = null;
                    effort.AddItem(subagents ? "Как у основного" : "Нет модели");
                    effort.SelectedIndex = 0;
                    effort.IsEnabled = false;
                    return;
                }
                var previousEffort = page.Selection?.ReasoningEffort ?? store.SelectedReasoningEffort;
                var provider = store.Providers.Profiles.FirstOrDefault(item => item.Id == pair.Item1);
                var model = provider?.Models.FirstOrDefault(item => item.Id == pair.Item2);
                if (model is null)
                {
                    page.Selection = new ModelSelection(pair.Item1, pair.Item2, previousEffort);
                    effort.AddItem(previousEffort);
                    effort.SelectedIndex = 0;
                    effort.IsEnabled = false;
                    return;
                }
                foreach (var value in model.ReasoningEfforts) effort.AddItem(value);
                var index = model.ReasoningEfforts.IndexOf(previousEffort);
                effort.SelectedIndex = index < 0 ? 0 : index;
                effort.IsEnabled = true;
                page.Selection = new ModelSelection(pair.Item1, pair.Item2, model.ReasoningEfforts[effort.SelectedIndex]);
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
