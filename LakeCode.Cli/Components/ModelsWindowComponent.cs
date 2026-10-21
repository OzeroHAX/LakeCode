using LakeCode.Cli.State;
using LakeCode.Core.Agent;
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
    private static readonly Color Surface = ProviderWindowControls.Surface;
    private static readonly Color Highlight = ProviderWindowControls.Highlight;

    public void Show(ConsoleWindowSystem windowSystem)
    {
        var initialMain = string.IsNullOrEmpty(store.SelectedProviderId) ? null
            : new ModelSelection(store.SelectedProviderId, store.SelectedModel.Id, store.SelectedReasoningEffort);
        var main = new ChatModelPicker(store.Providers, initialMain, null, store.SelectedReasoningEffort);
        var planning = new ChatModelPicker(store.Providers, store.PlanModel, "Как у основного агента", store.SelectedReasoningEffort);
        var children = new ChatModelPicker(store.Providers, store.SubagentModel, "Как у основного агента", store.SelectedReasoningEffort);
        var compaction = new ChatModelPicker(store.Providers, store.CompactionModel, "Не назначена", store.SelectedReasoningEffort,
            notice: "[grey70]Платно · история уходит выбранному подключению[/]");
        var roles = new[] { main, planning, children, compaction };
        var classifier = new ClassifierModelPage(store.Providers, store.Settings.Classifier);
        var images = new ImageGenerationModelPage(store.Providers, store.ImageGeneration);
        var classifierSection = Section();
        classifierSection.AddControl(classifier.Panel);
        var imagesSection = Section();
        imagesSection.AddControl(images.Panel);
        var other = ProviderWindowControls.Form();
        other.AddControl(classifierSection);
        other.AddControl(imagesSection);
        var tabs = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            BackgroundColor = Surface, ForegroundColor = Color.White,
            ActiveFocusedBackgroundColor = Highlight, ActiveUnfocusedBackgroundColor = Highlight,
            ActiveFocusedForegroundColor = Color.White, ActiveUnfocusedForegroundColor = Color.White
        };
        tabs.AddTab("Основной", main.Panel, false);
        tabs.AddTab("План", planning.Panel, false);
        tabs.AddTab("Сабагенты", children.Panel, false);
        tabs.AddTab("Сжатие", compaction.Panel, false);
        tabs.AddTab("Прочее", other, false);
        var status = ProviderWindowControls.Label("");
        status.Visible = false;
        var cancel = ProviderWindowControls.Button("Отмена");
        var apply = ProviderWindowControls.Button("Сохранить");
        var buttons = HorizontalGridControl.ButtonRow([cancel, apply]);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Margin(2, 0, 2, 0);
        var busy = false;
        var closed = false;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var token = lifetime.Token;
        var window = new WindowBuilder(windowSystem)
            .WithTitle("Модель").WithSize(84, 22)
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(Surface).WithForegroundColor(Color.White)
            .AddControl(tabs).AddControl(status).AddControl(buttons)
            .AddControl(ProviderWindowControls.Label("[grey70]Enter в списке — сохранить · F2 — ручной ID · F5 — обновить · Esc — отмена[/]"))
            .OnClosed((_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); })
            .Build();
        cancel.Click += (_, _) => { if (!busy) windowSystem.CloseWindow(window); };
        apply.Click += (_, _) => _ = SaveAsync();
        foreach (var picker in roles) picker.Activated += () => { if (!busy) _ = SaveAsync(); };
        images.ManualRequested += () => { if (!busy) ShowManualImage(); };
        classifierSection.ExpandedChanged += (_, _) =>
        {
            if (classifierSection.IsExpanded) imagesSection.Collapse();
            UpdateSummaries();
        };
        imagesSection.ExpandedChanged += (_, _) =>
        {
            if (imagesSection.IsExpanded) classifierSection.Collapse();
            UpdateSummaries();
        };
        tabs.TabChanged += (_, _) => UpdateSummaries();
        classifier.Changed += UpdateSummaries;
        images.Changed += UpdateSummaries;
        window.PreviewKeyPressed += (_, e) =>
        {
            switch (e.KeyInfo.Key)
            {
                case ConsoleKey.Escape:
                    if (roles.Any(picker => picker.DropdownOpen) || classifier.DropdownOpen || images.DropdownOpen) return;
                    e.Handled = true;
                    if (classifierSection.IsExpanded) { classifierSection.Collapse(); return; }
                    if (imagesSection.IsExpanded) { imagesSection.Collapse(); return; }
                    if (!busy) windowSystem.CloseWindow(window);
                    return;
                case ConsoleKey.F2:
                    if (busy || ActivePicker() is not { } target) return;
                    e.Handled = true;
                    ShowManual(target);
                    return;
                case ConsoleKey.F5:
                    if (busy) return;
                    e.Handled = true;
                    RefreshProvider();
                    return;
            }
        };
        UpdateSummaries();
        windowSystem.AddWindow(window);
        window.FocusControl(tabs);

        ChatModelPicker? ActivePicker() => tabs.ActiveTabIndex is >= 0 and <= 3 ? roles[tabs.ActiveTabIndex] : null;

        void UpdateSummaries()
        {
            classifierSection.Title = "Классификатор · " + classifier.Summary;
            imagesSection.Title = "Картинки · " + images.Summary;
        }

        void Busy(bool value)
        {
            busy = value;
            tabs.IsEnabled = cancel.IsEnabled = apply.IsEnabled = !value;
        }

        void RefreshPickers()
        {
            foreach (var picker in roles) picker.Refresh();
            images.Refresh();
            UpdateSummaries();
        }

        void RefreshProvider()
        {
            if (tabs.ActiveTabIndex == 4)
            {
                if (!imagesSection.IsExpanded || images.ProviderId is not { } imageProvider)
                {
                    ShowStatus("[grey70]F5 обновляет список для чат-ролей и раздела «Картинки»[/]");
                    return;
                }
                _ = ProviderActionAsync(ct => store.Providers.RefreshDiscoveryAsync(imageProvider, ct), "Обновление списка…", "");
                return;
            }
            if (ActivePicker()?.CurrentProviderId is not { } providerId)
            {
                ShowStatus("[yellow]Выберите подключение.[/]");
                return;
            }
            _ = ProviderActionAsync(ct => store.Providers.RefreshDiscoveryAsync(providerId, ct), "Обновление списка…", "");
        }

        async Task ProviderActionAsync(Func<CancellationToken, Task> action, string running, string success)
        {
            if (busy || closed) return;
            Busy(true);
            ShowStatus($"[grey70]{Markup.Escape(running)}[/]");
            try
            {
                await Task.Run(() => action(token), token).ConfigureAwait(false);
                windowSystem.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    // Refresh does not replace a missing current assignment with another model.
                    store.Refresh();
                    RefreshPickers();
                    Busy(false);
                    ShowStatus(success.Length == 0 ? "" : $"[grey70]{Markup.Escape(success)}[/]");
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

        void ShowManual(ChatModelPicker picker)
        {
            var profiles = store.Providers.Profiles.Where(profile => profile.Kind != ProviderKind.TypeSafe)
                .OrderBy(profile => profile.Name, StringComparer.Ordinal).ToList();
            if (profiles.Count == 0)
            {
                ShowError(new InvalidDataException("Нет подключений."));
                return;
            }
            var providers = ProviderWindowControls.Dropdown("Подключение: ");
            foreach (var profile in profiles) providers.AddItem(profile.Name);
            providers.SelectedIndex = Math.Max(0, profiles.FindIndex(profile => profile.Id == picker.CurrentProviderId));
            var id = ProviderWindowControls.Field("API ID: ");
            var list = ProviderWindowControls.Label("");
            var refresh = ProviderWindowControls.Button("Обновить");
            var cancelManual = ProviderWindowControls.Button("Отмена");
            var allow = ProviderWindowControls.Button("Разрешить");
            var error = ProviderWindowControls.Label("");
            var modalBusy = false;
            var modalClosed = false;
            var modalLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var modal = ProviderWindowControls.Base(windowSystem, "Ручной ID", 64, 17)
                .AddControl(providers).AddControl(id).AddControl(list)
                .AddControl(ProviderWindowControls.Buttons(refresh))
                .AddControl(error).AddControl(ProviderWindowControls.Buttons(cancelManual, allow))
                .OnClosed((_, _) => { modalClosed = true; modalLifetime.Cancel(); modalLifetime.Dispose(); })
                .Build();
            string Describe()
            {
                var profile = profiles[Math.Max(0, providers.SelectedIndex)];
                return profile.Discovery is { } discovery
                    ? $"[grey70]Список: {discovery.Ids.Length} ID · {discovery.UpdatedAt.ToLocalTime():g}[/]"
                    : "[grey70]Список не загружен[/]";
            }
            list.Text = Describe();
            providers.SelectedIndexChanged += (_, _) => list.Text = Describe();
            cancelManual.Click += (_, _) => { if (!modalBusy) windowSystem.CloseWindow(modal); };
            modal.PreviewKeyPressed += (_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape || providers.IsDropdownOpen) return;
                e.Handled = true;
                if (!modalBusy) windowSystem.CloseWindow(modal);
            };
            refresh.Click += async (_, _) =>
            {
                if (modalBusy) return;
                modalBusy = true;
                refresh.IsEnabled = allow.IsEnabled = false;
                error.Text = "[grey70]Обновление…[/]";
                var profile = profiles[Math.Max(0, providers.SelectedIndex)];
                try
                {
                    await Task.Run(() => store.Providers.RefreshDiscoveryAsync(profile.Id, modalLifetime.Token),
                        modalLifetime.Token).ConfigureAwait(false);
                    windowSystem.EnqueueOnUIThread(() =>
                    {
                        if (modalClosed) return;
                        store.Refresh();
                        RefreshPickers();
                        var index = profiles.FindIndex(item => item.Id == profile.Id);
                        if (index >= 0 && store.Providers.Profiles.FirstOrDefault(item => item.Id == profile.Id) is { } updated)
                            profiles[index] = updated;
                        modalBusy = false;
                        refresh.IsEnabled = allow.IsEnabled = true;
                        error.Text = "";
                        list.Text = Describe();
                    });
                }
                catch (Exception exception)
                {
                    windowSystem.EnqueueOnUIThread(() =>
                    {
                        if (modalClosed) return;
                        modalBusy = false;
                        refresh.IsEnabled = allow.IsEnabled = true;
                        error.Text = $"[yellow]{Markup.Escape(exception is OperationCanceledException ? "Операция отменена." : exception.Message)}[/]";
                    });
                }
            };
            allow.Click += async (_, _) =>
            {
                if (modalBusy) return;
                var modelId = id.Input.Trim();
                if (!ProviderProfile.ValidModelId(modelId))
                {
                    error.Text = "[yellow]Введите корректный API ID.[/]";
                    return;
                }
                var profile = profiles[Math.Max(0, providers.SelectedIndex)];
                modalBusy = true;
                allow.IsEnabled = cancelManual.IsEnabled = false;
                error.Text = "[grey70]Сохранение…[/]";
                try
                {
                    await Task.Run(() => store.Providers.AllowManualModelAsync(profile.Id, modelId, modalLifetime.Token),
                        modalLifetime.Token).ConfigureAwait(false);
                    windowSystem.EnqueueOnUIThread(() =>
                    {
                        if (modalClosed) return;
                        store.Refresh();
                        RefreshPickers();
                        picker.Select(profile.Id, modelId);
                        UpdateSummaries();
                        ShowStatus("");
                        windowSystem.CloseWindow(modal);
                    });
                }
                catch (Exception exception)
                {
                    windowSystem.EnqueueOnUIThread(() =>
                    {
                        if (modalClosed) return;
                        modalBusy = false;
                        allow.IsEnabled = cancelManual.IsEnabled = true;
                        error.Text = $"[yellow]{Markup.Escape(exception is OperationCanceledException ? "Операция отменена." : exception.Message)}[/]";
                    });
                }
            };
            windowSystem.AddWindow(modal);
            modal.FocusControl(providers);
        }

        void ShowManualImage()
        {
            var profiles = store.Providers.Profiles
                .Where(profile => profile.Kind == ProviderKind.OpenAI && profile.ApiMode == OpenAIApiMode.Responses)
                .OrderBy(profile => profile.Name, StringComparer.Ordinal).ToList();
            if (profiles.Count == 0)
            {
                ShowError(new InvalidDataException("Нет подключений OpenAI Responses."));
                return;
            }
            var providers = ProviderWindowControls.Dropdown("Подключение: ");
            foreach (var profile in profiles) providers.AddItem(profile.Name);
            providers.SelectedIndex = Math.Max(0, profiles.FindIndex(profile => profile.Id == images.ProviderId));
            var id = ProviderWindowControls.Field("Image model ID: ");
            var error = ProviderWindowControls.Label("");
            var cancelManual = ProviderWindowControls.Button("Отмена");
            var accept = ProviderWindowControls.Button("Выбрать");
            var modal = ProviderWindowControls.Base(windowSystem, "Ручной image ID", 64, 13)
                .AddControl(providers).AddControl(id).AddControl(error)
                .AddControl(ProviderWindowControls.Buttons(cancelManual, accept))
                .Build();
            cancelManual.Click += (_, _) => windowSystem.CloseWindow(modal);
            modal.PreviewKeyPressed += (_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape || providers.IsDropdownOpen) return;
                e.Handled = true;
                windowSystem.CloseWindow(modal);
            };
            accept.Click += (_, _) =>
            {
                var imageId = id.Input.Trim();
                if (!ProviderProfile.ValidModelId(imageId))
                {
                    error.Text = "[yellow]Введите корректный ID.[/]";
                    return;
                }
                if (ModelCatalog.GetModelType(imageId) != ModelType.ImageGeneration)
                {
                    error.Text = "[yellow]Этот ID не описан как image-модель в каталоге.[/]";
                    return;
                }
                images.Select(profiles[Math.Max(0, providers.SelectedIndex)].Id, imageId);
                UpdateSummaries();
                windowSystem.CloseWindow(modal);
            };
            windowSystem.AddWindow(modal);
            modal.FocusControl(providers);
        }

        async Task SaveAsync()
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
            ShowStatus("[grey70]Сохранение…[/]");
            try
            {
                if (settings.SubagentModel != store.SubagentModel || settings.PlanModel != store.PlanModel ||
                    settings.CompactionModel != store.CompactionModel || settings.Classifier != store.Settings.Classifier ||
                    settings.ImageGeneration != store.ImageGeneration)
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

        void ShowStatus(string markup)
        {
            status.Text = markup;
            status.Visible = markup.Length > 0;
        }

        void ShowError(Exception error) => ShowStatus(
            $"[yellow]{Markup.Escape(error is OperationCanceledException ? "Операция отменена." : error.Message)}[/]");
    }

    private static CollapsiblePanel Section() => new()
    {
        Title = "", Collapsible = true, IsExpanded = false, ShowHeader = true,
        HeaderStyle = CollapsibleHeaderStyle.Borderless,
        HorizontalAlignment = HorizontalAlignment.Stretch, MaxContentHeight = 14,
        BackgroundColor = Surface, ForegroundColor = Color.White,
        FocusedBackgroundColor = Highlight, FocusedForegroundColor = Color.White,
        Margin = new Margin(2, 1, 2, 0)
    };
}
