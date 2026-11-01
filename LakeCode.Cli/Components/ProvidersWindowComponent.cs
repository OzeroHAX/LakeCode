using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Cli.State;
using LakeCode.Core.Providers;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Connection drafts, model discovery and explicit overrides. Credentials never enter the chat.</summary>
internal sealed class ProvidersWindowComponent(ProviderRegistry providers, ModelStore models, CancellationToken shutdown)
{
    public void Show(ConsoleWindowSystem system)
    {
        var list = Choices(12);
        list.VerticalAlignment = VerticalAlignment.Fill;
        var add = Button("Добавить");
        var edit = Button("Изменить");
        var delete = Button("Удалить");
        var close = Button("Закрыть");
        var status = Label("");
        var window = Base(system, "Провайдеры", 84, 22)
            .AddControl(list).AddControl(Buttons(add, edit, delete)).AddControl(status)
            .AddControl(Buttons(close)).AddControl(Label("[grey70]↑ ↓ — выбор · Enter — изменить · Esc — закрыть[/]"))
            .Build();
        add.Click += (_, _) => ChoosePreset(system, Refresh);
        edit.Click += (_, _) => EditSelected();
        list.ItemActivated += (_, _) => EditSelected();
        delete.Click += (_, _) =>
        {
            if (Selected() is { } profile) ConfirmDelete(system, profile, Refresh);
        };
        close.Click += (_, _) => system.CloseWindow(window);
        window.PreviewKeyPressed += (_, e) =>
        { if (e.KeyInfo.Key == ConsoleKey.Escape) { e.Handled = true; system.CloseWindow(window); } };
        list.SelectedIndexChanged += (_, _) => edit.IsEnabled = delete.IsEnabled = Selected() is not null;
        Refresh();
        system.AddWindow(window);
        window.FocusControl(list);

        ProviderProfile? Selected() => list.SelectedItem?.Tag is string id
            ? providers.Profiles.FirstOrDefault(item => item.Id == id) : null;
        void EditSelected() { if (Selected() is { } profile) Edit(system, profile, Refresh); }
        void Refresh()
        {
            var selected = list.SelectedItem?.Tag as string;
            list.ClearItems();
            foreach (var profile in providers.Profiles.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var protocol = ProtocolLabel(profile.Kind, profile.ApiMode);
                var total = profile.Kind == ProviderKind.TypeSafe ? profile.ClassificationModels.Length : profile.ModelEntries.Length;
                var ready = profile.Kind == ProviderKind.TypeSafe ? profile.ClassificationModels.Count(m => m.IsVersioned) : profile.Models.Length;
                list.AddItem(new ListItem(Markup.Escape($"{profile.Name} · {protocol} · готово {ready}, всего {total}")) { Tag = profile.Id });
                if (profile.Id == selected) list.SelectedIndex = list.Items.Count - 1;
            }
            if (list.Items.Count == 0) list.AddItem(new ListItem("Нет подключений. Нажмите «Добавить»."));
            if (list.SelectedIndex < 0 && providers.Profiles.Length > 0) list.SelectedIndex = 0;
            edit.IsEnabled = delete.IsEnabled = Selected() is not null;
            status.Text = "[grey70]«Добавить» — только готовые провайдеры. Кастомные подключения задаются в ~/.lakecode/providers.json. Ключи хранятся без шифрования.[/]";
        }
    }

    private void ChoosePreset(ConsoleWindowSystem system, Action changed)
    {
        var presets = ProviderPresets.All;
        var search = Field("Поиск: ");
        search.Placeholder = "имя, ID или URL";
        var list = Choices(14);
        list.VerticalAlignment = VerticalAlignment.Fill;
        var status = Label("");
        var choose = Button("Выбрать");
        var cancel = Button("Отмена");
        var window = Base(system, "Добавить провайдера", 84, 22)
            .AddControl(search).AddControl(list).AddControl(Buttons(choose, cancel)).AddControl(status)
            .AddControl(Label("[grey70]Введите запрос · Tab — список · Enter — выбрать · Esc — отмена[/]"))
            .Build();
        var updating = false;
        var closed = false;

        ProviderPreset? Selected() => list.SelectedItem?.Tag as ProviderPreset;
        void Pick()
        {
            if (closed || Selected() is not { } preset) return;
            closed = true;
            system.CloseWindow(window);
            CreatePreset(system, preset, changed);
        }
        void Refresh()
        {
            var selectedId = Selected()?.Id;
            updating = true;
            try
            {
                list.ClearItems();
                var query = search.Input.Trim();
                foreach (var preset in presets.Where(preset => Matches(preset, query)))
                {
                    list.AddItem(new ListItem(Markup.Escape($"{preset.Name} · {ModelsLabel(preset.ModelCount)}")) { Tag = preset });
                    if (preset.Id == selectedId) list.SelectedIndex = list.Items.Count - 1;
                }
                if (list.Items.Count == 0) list.AddItem(new ListItem("Ничего не найдено.") { IsEnabled = false });
                if (list.SelectedIndex < 0 && list.Items.Count > 0 && list.Items[0].IsEnabled) list.SelectedIndex = 0;
            }
            finally
            {
                updating = false;
            }
            choose.IsEnabled = Selected() is not null;
            var selected = Selected();
            status.Text = selected is null
                ? "[grey70]Начните вводить название или выберите строку.[/]"
                : Markup.Escape($"{selected.BaseUrl} · {ProtocolLabel(selected.Kind, selected.ApiMode)} · {ModelsLabel(selected.ModelCount)}");
        }

        search.InputChanged += (_, _) => { if (!updating) Refresh(); };
        list.SelectedIndexChanged += (_, _) => { if (!updating) choose.IsEnabled = Selected() is not null; };
        list.ItemActivated += (_, _) => Pick();
        choose.Click += (_, _) => Pick();
        cancel.Click += (_, _) => { closed = true; system.CloseWindow(window); };
        window.OnClosing += (_, _) => closed = true;
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Escape)
            {
                e.Handled = true;
                closed = true;
                system.CloseWindow(window);
            }
            else if (e.KeyInfo.Key == ConsoleKey.Enter && window.FocusManager.IsInFocusPath(search))
            {
                e.Handled = true;
                Pick();
            }
        };
        Refresh();
        system.AddWindow(window);
        window.FocusControl(search);

        static bool Matches(ProviderPreset preset, string query) => query.Length == 0 ||
            preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            preset.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            preset.BaseUrl.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void CreatePreset(ConsoleWindowSystem system, ProviderPreset preset, Action changed)
    {
        var name = Field("Название: ", preset.Name);
        var key = Field("API key: ");
        key.MaskCharacter = '*';
        key.MaxHistoryEntries = 0;
        key.Placeholder = preset.EnvironmentVariables.FirstOrDefault() is { } variable
            ? $"Введите ключ ({variable})"
            : "Введите ключ";
        var status = Label("");
        var save = Button("Сохранить");
        var cancel = Button("Отмена");
        var window = Base(system, "Новый провайдер", 78, 17)
            .AddControl(Label(Markup.Escape($"{preset.Name} · {ProtocolLabel(preset.Kind, preset.ApiMode)}")))
            .AddControl(Label(Markup.Escape(preset.BaseUrl)))
            .AddControl(name).AddControl(key)
            .AddControl(Label($"[grey70]Модели: {ModelsLabel(preset.ModelCount)} из каталога models.dev. Выбрать модель — /models.[/]"))
            .AddControl(status).AddControl(Buttons(cancel, save))
            .AddControl(Label("[grey70]Ключ хранится локально без шифрования. Tab — поля · Esc — отмена.[/]"))
            .Build();
        var busy = false;
        var closed = false;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        window.OnClosing += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
            key.Input = "";
            key.ClearHistory();
        };
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.Escape) return;
            e.Handled = true;
            if (!busy) system.CloseWindow(window);
        };
        cancel.Click += (_, _) => { if (!busy) system.CloseWindow(window); };
        save.Click += (_, _) => _ = SaveAsync();

        async Task SaveAsync()
        {
            if (busy) return;
            var profile = preset.CreateProfile(name.Input.Trim(), key.Input);
            try
            {
                if (string.IsNullOrWhiteSpace(profile.ApiKey)) throw new InvalidDataException("Введите API-ключ.");
                profile.Validate();
            }
            catch (Exception error)
            {
                status.Text = $"[yellow]{Markup.Escape(error.Message)}[/]";
                window.FocusControl(string.IsNullOrWhiteSpace(profile.ApiKey) ? key : name);
                return;
            }
            busy = true;
            save.IsEnabled = cancel.IsEnabled = name.IsEnabled = key.IsEnabled = false;
            status.Text = "[grey70]Сохранение…[/]";
            try
            {
                await Task.Run(() => providers.SaveAsync(profile, lifetime.Token), lifetime.Token).ConfigureAwait(false);
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    models.Refresh();
                    changed();
                    system.CloseWindow(window);
                });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    busy = false;
                    save.IsEnabled = cancel.IsEnabled = name.IsEnabled = key.IsEnabled = true;
                    status.Text = "[yellow]Не удалось сохранить провайдера. Проверьте доступ к ~/.lakecode/providers.json.[/]";
                });
            }
        }

        system.AddWindow(window);
        window.FocusControl(key);
    }

    private void Edit(ConsoleWindowSystem system, ProviderProfile original, Action changed)
    {
        var name = Field("Название: ", original.Name);
        var key = Field("API key: ");
        key.MaskCharacter = '*';
        key.MaxHistoryEntries = 0;
        key.Placeholder = "Пустое поле — сохранить текущий ключ";
        var connectionForm = Form();
        connectionForm.AddControl(name);
        connectionForm.AddControl(Label(Markup.Escape($"{ProtocolLabel(original.Kind, original.ApiMode)} · {original.BaseUrl}")));
        connectionForm.AddControl(Label(original.CatalogProviderId is { } catalog
            ? $"[grey70]Пресет models.dev: {Markup.Escape(catalog)}. Подключение не редактируется здесь.[/]"
            : "[grey70]Кастомное подключение: URL и протокол задаются в ~/.lakecode/providers.json.[/]"));
        connectionForm.AddControl(key);
        connectionForm.AddControl(Label("[grey70]Ключ хранится локально без шифрования. HTTP не шифрует ключ и сообщения.[/]"));

        var discovery = original.Discovery;
        ProviderProfile? discoveryConnection = original;
        var drafts = original.ModelOverrides
            .ToDictionary(item => item.Key, item => ProviderModelEditor.Draft.FromRule(item.Value), StringComparer.Ordinal);
        var classificationIds = original.ManualClassificationModels.ToHashSet(StringComparer.Ordinal);
        var editingClassifier = original.Kind == ProviderKind.TypeSafe;
        var modelList = Choices(5);
        var modelEditor = new ProviderModelEditor(CatalogBasis());
        var selectedLabel = Label("");
        var discoveryStatus = Label("");
        var refresh = Button("Обновить список");
        var manualId = Field("Ручной API ID: ");
        var addManual = Button("Добавить ID");
        var removeManual = Button("Удалить ручной ID");
        var reset = Button("Сбросить override");
        var modelsForm = Form();
        modelsForm.AddControl(Buttons(refresh));
        modelsForm.AddControl(discoveryStatus);
        modelsForm.AddControl(modelList);
        modelsForm.AddControl(manualId);
        modelsForm.AddControl(Buttons(addManual, removeManual, reset));
        modelsForm.AddControl(selectedLabel);
        modelsForm.AddControl(modelEditor.Control);
        var tabs = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            BackgroundColor = Surface, ForegroundColor = Color.White, Outline = false,
            HeaderStyle = TabHeaderStyle.Separator, ActiveFocusedBackgroundColor = Highlight,
            ActiveUnfocusedBackgroundColor = Highlight, ActiveFocusedForegroundColor = Color.White,
            ActiveUnfocusedForegroundColor = Color.White
        };
        tabs.AddTab("Подключение", connectionForm, false);
        tabs.AddTab("Модели", modelsForm, false);
        var status = Label("");
        var save = Button("Сохранить");
        var cancel = Button("Отмена");
        var busy = false;
        var saving = false;
        var closed = false;
        var rebuilding = false;
        string? currentId = null;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var window = Base(system, "Настройки провайдера", 94, 32)
            .AddControl(tabs).AddControl(status).AddControl(Buttons(cancel, save))
            .AddControl(Label("[grey70]Tab — поля · ← → — вкладки · Esc — отмена. Изменения применяются после сохранения.[/]"))
            .Build();
        window.OnClosing += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
            key.Input = "";
            key.ClearHistory();
        };
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.Escape) return;
            if (modelEditor.DropdownOpen) return;
            e.Handled = true;
            if (!saving) system.CloseWindow(window);
        };
        cancel.Click += (_, _) => { if (!saving) system.CloseWindow(window); };
        save.Click += (_, _) => _ = SaveAsync();
        refresh.Click += (_, _) => _ = DiscoverAsync();
        modelList.SelectedIndexChanged += (_, _) =>
        {
            if (rebuilding) return;
            Capture();
            LoadSelected();
        };
        addManual.Click += (_, _) =>
        {
            var apiId = manualId.Input.Trim();
            if (!ProviderProfile.ValidModelId(apiId)) { Error("Введите корректный API ID модели."); return; }
            Capture();
            if (editingClassifier)
            {
                if (!ClassificationModel.IsVersionedId(apiId)) { Error("Укажите фиксированный ID jev-X.Y.Z, а не алиас."); return; }
                classificationIds.Add(apiId);
                manualId.Input = "";
                RefreshModels(apiId);
                return;
            }
            var draft = drafts.GetValueOrDefault(apiId) ?? ProviderModelEditor.Draft.FromRule(null);
            drafts[apiId] = draft with { Manual = true };
            manualId.Input = "";
            RefreshModels(apiId);
        };
        removeManual.Click += (_, _) =>
        {
            if (currentId is null) return;
            Capture();
            if (editingClassifier) classificationIds.Remove(currentId);
            else drafts.Remove(currentId);
            RefreshModels();
        };
        reset.Click += (_, _) =>
        {
            if (currentId is null) return;
            Capture();
            var manual = drafts[currentId].Manual;
            drafts[currentId] = ProviderModelEditor.Draft.FromRule(null) with { Manual = manual };
            modelEditor.Load(currentId, drafts[currentId]);
        };
        tabs.TabChanged += (_, _) =>
        {
            Capture();
            if (tabs.ActiveTabIndex == 1) modelEditor.SetCatalog(CatalogBasis());
            RefreshModels();
        };
        RefreshModels();
        system.AddWindow(window);
        window.FocusControl(name);

        ProviderProfile Connection() => original with
        {
            Name = name.Input.Trim(),
            ApiKey = key.Input.Length == 0 ? original.ApiKey : key.Input
        };

        IReadOnlyDictionary<string, ModelInfo> CatalogBasis()
        {
            var connection = Connection();
            var providerId = connection.CatalogProviderId;
            if (providerId is null || !ModelsDevCatalog.Providers.TryGetValue(providerId, out var provider))
                return new Dictionary<string, ModelInfo>();
            return provider.Models.Values.ToDictionary(
                model => model.Id,
                model => ModelsDevCatalog.ToModelInfo(model, connection.Kind),
                StringComparer.Ordinal);
        }
        bool SameConnection(ProviderProfile profile) => discoveryConnection is not null && profile.Kind == discoveryConnection.Kind &&
            profile.BaseUrl.TrimEnd('/') == discoveryConnection.BaseUrl.TrimEnd('/') && profile.ApiKey == discoveryConnection.ApiKey &&
            profile.DiscoveryUrl == discoveryConnection.DiscoveryUrl;
        DiscoveredModels? CurrentDiscovery() => SameConnection(Connection()) ? discovery : null;
        void Capture()
        {
            if (currentId is null || editingClassifier || LakeCode.Core.Agent.ModelCatalog.GetModelType(currentId) == LakeCode.Core.Agent.ModelType.ImageGeneration) return;
            drafts[currentId] = modelEditor.Capture();
            var row = modelList.Items.FirstOrDefault(item => item.Tag as string == currentId);
            if (row is not null) row.Text = ModelCaption(currentId);
        }
        string ModelCaption(string apiId)
        {
            if (editingClassifier)
                return Markup.Escape($"{apiId} · Классификация" +
                    (ClassificationModel.IsVersionedId(apiId) ? " · фиксированная версия" : " · алиас, для назначения нужен ID версии") +
                    (classificationIds.Contains(apiId) ? " · вручную" : ""));
            string text;
            try
            {
                var rule = drafts.GetValueOrDefault(apiId)?.ToRule();
                var connection = Connection();
                var entry = ModelResolver.Resolve(connection with
                {
                    Discovery = SameConnection(connection) ? discovery : null,
                    ModelOverrides = rule is null ? ImmutableDictionary<string, ModelOverride>.Empty
                        : ImmutableDictionary<string, ModelOverride>.Empty.Add(apiId, rule)
                }, apiId);
                text = ModelResolver.ListingDescription(entry.Listing);
                text += entry.Error is not null ? " · ошибка" : " · " + entry.Source;
            }
            catch (InvalidDataException) { text = "Нужно настроить"; }
            return Markup.Escape($"{apiId} · {text}");
        }
        void LoadSelected()
        {
            currentId = modelList.SelectedItem?.Tag as string;
            var imageModel = currentId is not null && LakeCode.Core.Agent.ModelCatalog.GetModelType(currentId) == LakeCode.Core.Agent.ModelType.ImageGeneration;
            modelEditor.Control.Visible = currentId is not null && !editingClassifier && !imageModel;
            removeManual.IsEnabled = currentId is not null && (editingClassifier
                ? classificationIds.Contains(currentId) : CurrentDiscovery()?.Ids.Contains(currentId, StringComparer.Ordinal) != true);
            reset.Visible = !editingClassifier;
            reset.IsEnabled = currentId is not null && !editingClassifier;
            selectedLabel.Text = currentId is null ? "" : Markup.Escape("API ID: " + currentId);
            if (imageModel)
            {
                selectedLabel.Text = Markup.Escape($"API ID: {currentId} · Генерация изображений. Назначение: /models → Изображения. Чат-характеристики не требуются.");
                return;
            }
            if (currentId is null || editingClassifier) return;
            if (!drafts.ContainsKey(currentId)) drafts[currentId] = ProviderModelEditor.Draft.FromRule(null);
            modelEditor.Load(currentId, drafts[currentId]);
        }
        void RefreshModels(string? select = null)
        {
            rebuilding = true;
            var selected = select ?? currentId;
            currentId = null;
            modelList.ClearItems();
            var visibleDiscovery = CurrentDiscovery();
            IEnumerable<string> catalogIds = !editingClassifier && Connection().CatalogProviderId is { } catalogProviderId
                ? ModelsDevCatalog.ModelIdsForProvider(catalogProviderId)
                : [];
            IEnumerable<string> manualIds = editingClassifier ? classificationIds : drafts.Keys;
            foreach (var apiId in catalogIds.Concat(visibleDiscovery?.Ids ?? []).Concat(manualIds)
                         .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                modelList.AddItem(new ListItem(ModelCaption(apiId)) { Tag = apiId });
                if (apiId == selected) modelList.SelectedIndex = modelList.Items.Count - 1;
            }
            if (modelList.Items.Count > 0 && modelList.SelectedIndex < 0) modelList.SelectedIndex = 0;
            if (modelList.Items.Count == 0) modelList.AddItem(new ListItem("Обновите список или добавьте API ID вручную."));
            discoveryStatus.Text = editingClassifier
                ? "[grey70]System One: discovery может вернуть только алиасы. Для классификатора добавьте фиксированный ID, например jev-1.13.0. Reasoning и чат-параметры не нужны.[/]"
                : Markup.Escape(DiscoveryText(Connection() with { Discovery = visibleDiscovery }));
            rebuilding = false;
            LoadSelected();
        }
        void Busy(bool value, bool writing = false)
        {
            busy = value;
            saving = value && writing;
            tabs.IsEnabled = save.IsEnabled = !value;
            cancel.IsEnabled = !saving;
        }
        void Error(string message) => status.Text = $"[yellow]{Markup.Escape(message)}[/]";

        async Task DiscoverAsync()
        {
            if (busy) return;
            Capture();
            ProviderProfile connection;
            try { connection = Connection(); connection.Validate(); }
            catch (Exception error) { tabs.ActiveTabIndex = 0; Error(error.Message); return; }
            Busy(true);
            status.Text = "[grey70]Получение моделей…[/]";
            try
            {
                var result = await ProviderModelDiscovery.DiscoverAsync(connection, lifetime.Token).ConfigureAwait(false);
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    discovery = result;
                    discoveryConnection = connection;
                    Busy(false);
                    status.Text = "[grey70]Список получен. Настройте неизвестные модели и сохраните провайдера.[/]";
                    RefreshModels();
                });
            }
            catch (Exception error)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    Busy(false);
                    Error(error is InvalidDataException ? error.Message : "Не удалось получить модели. Предыдущий список и черновики сохранены.");
                });
            }
        }

        async Task SaveAsync()
        {
            if (busy) return;
            Capture();
            ProviderProfile connection;
            try
            {
                connection = Connection();
                connection.Validate();
                if (string.IsNullOrWhiteSpace(connection.ApiKey)) throw new InvalidDataException("Введите API-ключ.");
            }
            catch (Exception error) { tabs.ActiveTabIndex = 0; Error(error.Message); return; }
            var rules = ImmutableDictionary.CreateBuilder<string, ModelOverride>(StringComparer.Ordinal);
            foreach (var (apiId, draft) in editingClassifier ? new Dictionary<string, ProviderModelEditor.Draft>() : drafts)
            {
                try
                {
                    if (draft.ToRule() is not { } rule) continue;
                    var entry = ModelResolver.Resolve(connection with { ModelOverrides = ImmutableDictionary<string, ModelOverride>.Empty.Add(apiId, rule) }, apiId);
                    if (entry.Error is not null) throw new InvalidDataException(entry.Error);
                    rules[apiId] = rule;
                }
                catch (Exception error)
                {
                    tabs.ActiveTabIndex = 1;
                    RefreshModels(apiId);
                    Error($"{apiId}: {error.Message}");
                    return;
                }
            }
            var profile = connection with
            {
                ModelOverrides = rules.ToImmutable(), Discovery = SameConnection(connection) ? discovery : null,
                ManualClassificationModels = editingClassifier ? classificationIds.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray() : []
            };
            Busy(true, writing: true);
            status.Text = "[grey70]Сохранение…[/]";
            try
            {
                await Task.Run(() => providers.SaveAsync(profile, lifetime.Token), lifetime.Token).ConfigureAwait(false);
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    models.Refresh();
                    changed();
                    system.CloseWindow(window);
                });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    Busy(false);
                    Error("Не удалось сохранить провайдера. Проверьте доступ к ~/.lakecode/providers.json и model-cache; черновики сохранены.");
                });
            }
        }
    }

    private void ConfirmDelete(ConsoleWindowSystem system, ProviderProfile profile, Action changed)
    {
        var cancel = Button("Отмена");
        var delete = Button("Удалить");
        var status = Label("");
        var busy = false;
        var window = Base(system, "Удаление провайдера", 74, 12)
            .AddControl(Label(Markup.Escape(profile.Name)))
            .AddControl(Label("История останется. Назначения моделей и восстановленная очередь этого провайдера станут недоступны."))
            .AddControl(status).AddControl(Buttons(cancel, delete)).Build();
        cancel.Click += (_, _) => { if (!busy) system.CloseWindow(window); };
        window.PreviewKeyPressed += (_, e) =>
        { if (e.KeyInfo.Key == ConsoleKey.Escape) { e.Handled = true; if (!busy) system.CloseWindow(window); } };
        delete.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            cancel.IsEnabled = delete.IsEnabled = false;
            try
            {
                await Task.Run(() => providers.DeleteAsync(profile.Id, shutdown), shutdown).ConfigureAwait(false);
                system.EnqueueOnUIThread(() => { models.Refresh(); changed(); system.CloseWindow(window); });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() =>
                {
                    busy = false;
                    cancel.IsEnabled = delete.IsEnabled = true;
                    status.Text = "[yellow]Не удалось удалить провайдера. Проверьте доступ к файлу настроек.[/]";
                });
            }
        };
        system.AddWindow(window);
        window.FocusControl(cancel);
    }

}
