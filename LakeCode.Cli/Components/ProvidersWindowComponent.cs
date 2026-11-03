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
                var protocol = profile.Definition.ProtocolLabel;
                var total = profile.ModelEntries.Length + profile.Definition.DecisionModels(profile).Length;
                var ready = profile.Models.Length + profile.Definition.DecisionModels(profile).Count(m => m.IsSelectable);
                list.AddItem(new ListItem(Markup.Escape($"{profile.Name} · {protocol} · готово {ready}, всего {total}")) { Tag = profile.Id });
                if (profile.Id == selected) list.SelectedIndex = list.Items.Count - 1;
            }
            if (list.Items.Count == 0) list.AddItem(new ListItem("Нет подключений. Нажмите «Добавить»."));
            if (list.SelectedIndex < 0 && providers.Profiles.Length > 0) list.SelectedIndex = 0;
            edit.IsEnabled = delete.IsEnabled = Selected() is not null;
            status.Text = "[grey70]«Добавить» — провайдеры из каталога. Учётные данные хранятся локально без шифрования.[/]";
        }
    }

    private void ChoosePreset(ConsoleWindowSystem system, Action changed)
    {
        var connected = providers.Profiles.Select(profile => profile.DefinitionId).ToHashSet(StringComparer.Ordinal);
        var presets = ProviderCatalog.All.Where(preset => !connected.Contains(preset.Id)).ToImmutableArray();
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

        Provider? Selected() => list.SelectedItem?.Tag as Provider;
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
                : Markup.Escape($"{selected.BaseUrl} · {selected.ProtocolLabel} · {ModelsLabel(selected.ModelCount)}");
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

        static bool Matches(Provider preset, string query) => query.Length == 0 ||
            preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            preset.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            preset.BaseUrl.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void CreatePreset(ConsoleWindowSystem system, Provider preset, Action changed)
    {
        var name = Field("Название: ", preset.Name);
        var key = Field("API key: ");
        key.MaskCharacter = '*';
        key.MaxHistoryEntries = 0;
        key.Placeholder = preset.EnvironmentVariables.FirstOrDefault() is { } variable
            ? $"Введите ключ ({variable})"
            : "Введите ключ";
        var methods = preset.AuthenticationMethods;
        var auth = Dropdown("Вход: ", methods.Select(method => method.Name).ToArray());
        auth.SelectedIndex = 0;
        auth.Visible = methods.Length > 1;
        var import = Button("Импортировать");
        import.Visible = false;
        var authHint = Label("");
        var endpointLabel = Label(Markup.Escape(preset.BaseUrl));
        ProviderProfile? imported = null;
        var status = Label("");
        var save = Button("Сохранить");
        var cancel = Button("Отмена");
        var window = Base(system, "Новый провайдер", 78, 22)
            .AddControl(Label(Markup.Escape($"{preset.Name} · {preset.ProtocolLabel}")))
            .AddControl(endpointLabel)
            .AddControl(name).AddControl(auth).AddControl(key).AddControl(import).AddControl(authHint)
            .AddControl(Label(!preset.SupportsChat
                ? "[grey70]Модели добавляются или загружаются после сохранения. Выбрать модель — /models.[/]"
                : $"[grey70]Модели: {ModelsLabel(preset.ModelCount)} из каталога models.dev. Выбрать модель — /models.[/]"))
            .AddControl(status).AddControl(Buttons(cancel, save))
            .AddControl(Label("[grey70]Учётные данные хранятся локально без шифрования. Tab — поля · Esc — отмена.[/]"))
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
        auth.SelectedIndexChanged += (_, _) => UpdateAuth();
        import.Click += (_, _) => _ = ImportAsync();
        UpdateAuth();

        ProviderAuthMethod SelectedMethod() => methods[Math.Clamp(auth.SelectedIndex, 0, methods.Length - 1)];
        void UpdateAuth()
        {
            var method = SelectedMethod();
            key.Visible = !method.RequiresImport;
            import.Visible = method.RequiresImport;
            authHint.Visible = method.Hint is not null;
            endpointLabel.Text = Markup.Escape(preset.EndpointFor(method.Id));
            authHint.Text = method.Hint is null ? "" : $"[grey70]{Markup.Escape(method.Hint)}[/]";
        }
        async Task ImportAsync()
        {
            if (busy) return;
            var method = SelectedMethod();
            busy = true;
            save.IsEnabled = cancel.IsEnabled = auth.IsEnabled = import.IsEnabled = false;
            status.Text = "[grey70]Импорт авторизации…[/]";
            try
            {
                var draft = preset.CreateProfile(name.Input.Trim(), "");
                var result = await Task.Run(() => preset.ImportCredentialsAsync(draft, method.Id, lifetime.Token), lifetime.Token).ConfigureAwait(false);
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    imported = result;
                    busy = false;
                    save.IsEnabled = cancel.IsEnabled = auth.IsEnabled = import.IsEnabled = true;
                    status.Text = "[green]Данные импортированы. Сохраните подключение.[/]";
                });
            }
            catch (Exception error)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    busy = false;
                    save.IsEnabled = cancel.IsEnabled = auth.IsEnabled = import.IsEnabled = true;
                    status.Text = $"[yellow]{Markup.Escape(error is InvalidDataException ? error.Message : "Не удалось импортировать авторизацию.")}[/]";
                });
            }
        }

        async Task SaveAsync()
        {
            if (busy) return;
            var method = SelectedMethod();
            var profile = method.RequiresImport ? imported : preset.CreateProfile(name.Input.Trim(), key.Input);
            if (profile is null || preset.AuthenticationMethod(profile) != method.Id)
            {
                status.Text = "[yellow]Сначала импортируйте авторизацию.[/]";
                window.FocusControl(import);
                return;
            }
            profile = profile with { Name = name.Input.Trim() };
            try
            {
                if (!profile.HasCredentials) throw new InvalidDataException("Настройте авторизацию.");
                profile.Validate();
            }
            catch (Exception error)
            {
                status.Text = $"[yellow]{Markup.Escape(error.Message)}[/]";
                window.FocusControl(!profile.HasCredentials ? (method.RequiresImport ? import : key) : name);
                return;
            }
            busy = true;
            save.IsEnabled = cancel.IsEnabled = name.IsEnabled = key.IsEnabled = auth.IsEnabled = import.IsEnabled = false;
            status.Text = "[grey70]Сохранение…[/]";
            try
            {
                await Task.Run(() => providers.SaveAsync(profile, lifetime.Token), lifetime.Token).ConfigureAwait(false);
                string? discoveryError = null;
                if (method.RequiresImport)
                {
                    try { await providers.RefreshDiscoveryAsync(profile.Id, lifetime.Token).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { discoveryError = error is InvalidDataException ? error.Message : "Не удалось получить список моделей."; }
                }
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    models.Refresh();
                    changed();
                    if (discoveryError is null) system.CloseWindow(window);
                    else
                    {
                        busy = false;
                        cancel.Text = "Закрыть";
                        cancel.IsEnabled = true;
                        status.Text = $"[yellow]Подключение сохранено. {Markup.Escape(discoveryError)}[/]";
                    }
                });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    busy = false;
                    save.IsEnabled = cancel.IsEnabled = name.IsEnabled = key.IsEnabled = auth.IsEnabled = import.IsEnabled = true;
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
        var methods = original.Definition.AuthenticationMethods;
        var auth = Dropdown("Вход: ", methods.Select(method => method.Name).ToArray());
        var originalMethod = original.Definition.AuthenticationMethod(original);
        auth.SelectedIndex = Math.Max(0, Array.FindIndex(methods.ToArray(), method => method.Id == originalMethod));
        auth.Visible = methods.Length > 1;
        var import = Button("Импортировать");
        var authHint = Label("");
        var endpointLabel = Label(Markup.Escape($"{original.Definition.ProtocolLabel} · {original.Definition.EndpointFor(originalMethod)}"));
        ProviderProfile? imported = methods[auth.SelectedIndex].RequiresImport ? original : null;
        var connectionForm = Form();
        connectionForm.AddControl(name);
        connectionForm.AddControl(endpointLabel);
        connectionForm.AddControl(Label(original.Definition.CatalogProviderId is { Length: > 0 } catalog
            ? $"[grey70]Пресет models.dev: {Markup.Escape(catalog)}. Подключение не редактируется здесь.[/]"
            : "[grey70]Настройки подключения определяются провайдером.[/]"));
        connectionForm.AddControl(auth);
        connectionForm.AddControl(key);
        connectionForm.AddControl(import);
        connectionForm.AddControl(authHint);
        connectionForm.AddControl(Label("[grey70]Учётные данные хранятся локально без шифрования.[/]"));

        var discovery = original.Discovery;
        ProviderProfile? discoveryConnection = original;
        var editingClassifier = original.Definition.SupportsClassification && !original.Definition.SupportsChat;
        var drafts = original.ModelOverrides.Where(item => !item.Value.Decision)
            .ToDictionary(item => item.Key, item => ProviderModelEditor.Draft.FromRule(item.Value), StringComparer.Ordinal);
        var classificationIds = original.ModelOverrides.Where(item => item.Value.Manual && (editingClassifier || item.Value.Decision))
            .Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var modelList = Choices(5);
        var modelEditor = new ProviderModelEditor(CatalogBasis());
        var selectedLabel = Label("");
        var discoveryStatus = Label("");
        var refresh = Button("Обновить список");
        var manualId = Field("Ручной API ID: ");
        var addManual = Button("Добавить ID");
        var addDecision = Button("Добавить ID решения");
        addDecision.Visible = original.Definition.SupportsClassification && original.Definition.SupportsChat;
        var removeManual = Button("Удалить ручной ID");
        var reset = Button("Сбросить override");
        var modelsForm = Form();
        modelsForm.AddControl(Buttons(refresh));
        modelsForm.AddControl(discoveryStatus);
        modelsForm.AddControl(modelList);
        modelsForm.AddControl(manualId);
        modelsForm.AddControl(Buttons(addManual, addDecision, removeManual, reset));
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
        auth.SelectedIndexChanged += (_, _) => UpdateAuth();
        import.Click += (_, _) => _ = ImportAsync();
        UpdateAuth();
        modelList.SelectedIndexChanged += (_, _) =>
        {
            if (rebuilding) return;
            Capture();
            LoadSelected();
        };
        addManual.Click += (_, _) =>
        {
            var apiId = manualId.Input.Trim();
            if (!ProviderModel.ValidId(apiId)) { Error("Введите корректный API ID модели."); return; }
            Capture();
            if (editingClassifier)
            {
                AddDecision(apiId);
                return;
            }
            var modelConnection = Connection() with { Discovery = CurrentDiscovery() };
            if (!modelConnection.Definition.IsModelAllowed(modelConnection, apiId))
            { Error("Модель отсутствует в списке выбранного способа входа. Обновите список моделей."); return; }
            if (IsDecision(apiId)) { Error("Этот ID уже используется моделью решений."); return; }
            var draft = drafts.GetValueOrDefault(apiId) ?? ProviderModelEditor.Draft.FromRule(null);
            drafts[apiId] = draft with { Manual = true };
            manualId.Input = "";
            RefreshModels(apiId);
        };
        addDecision.Click += (_, _) => AddDecision(manualId.Input.Trim());
        void AddDecision(string apiId)
        {
            if (!ProviderModel.ValidId(apiId) || !Connection().Definition.CanSelectDecisionModel(apiId))
            { Error("Укажите поддерживаемый ID модели решений."); return; }
            Capture();
            if (drafts.ContainsKey(apiId)) { Error("Этот ID уже используется чат-моделью."); return; }
            classificationIds.Add(apiId);
            manualId.Input = "";
            RefreshModels(apiId);
        }
        removeManual.Click += (_, _) =>
        {
            if (currentId is null) return;
            Capture();
            if (classificationIds.Contains(currentId)) classificationIds.Remove(currentId);
            else drafts.Remove(currentId);
            RefreshModels();
        };
        reset.Click += (_, _) =>
        {
            if (currentId is null || !drafts.ContainsKey(currentId)) return;
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

        ProviderAuthMethod SelectedMethod() => methods[Math.Clamp(auth.SelectedIndex, 0, methods.Length - 1)];
        void UpdateAuth()
        {
            var method = SelectedMethod();
            key.Visible = !method.RequiresImport;
            import.Visible = method.RequiresImport;
            authHint.Visible = method.Hint is not null;
            endpointLabel.Text = Markup.Escape($"{original.Definition.ProtocolLabel} · {original.Definition.EndpointFor(method.Id)}");
            authHint.Text = method.Hint is null ? "" : $"[grey70]{Markup.Escape(method.Hint)}[/]";
        }
        async Task ImportAsync()
        {
            if (busy) return;
            var method = SelectedMethod();
            Busy(true);
            status.Text = "[grey70]Импорт авторизации…[/]";
            try
            {
                var result = await Task.Run(() => original.Definition.ImportCredentialsAsync(original, method.Id, lifetime.Token), lifetime.Token).ConfigureAwait(false);
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    imported = result;
                    Busy(false);
                    status.Text = "[green]Данные импортированы. Сохраните подключение.[/]";
                });
            }
            catch (Exception error)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    Busy(false);
                    Error(error is InvalidDataException ? error.Message : "Не удалось импортировать авторизацию.");
                });
            }
        }
        ProviderProfile Connection()
        {
            var profile = original with { Name = name.Input.Trim() };
            var method = SelectedMethod();
            if (method.RequiresImport)
                return imported is not null && original.Definition.AuthenticationMethod(imported) == method.Id
                    ? imported with { Name = profile.Name } : profile with { Credentials = ImmutableDictionary<string, string>.Empty };
            return key.Input.Length == 0 && originalMethod == method.Id
                ? profile : profile.Definition.UpdateCredential(profile, key.Input);
        }

        IReadOnlyDictionary<string, ModelInfo> CatalogBasis()
        {
            var connection = Connection() with { Discovery = CurrentDiscovery(), ModelOverrides = ImmutableDictionary<string, ModelOverride>.Empty };
            return connection.Definition.Models.Where(model => connection.Definition.IsModelAllowed(connection, model.Id))
                .Select(model => ModelResolver.Resolve(connection, model.Id).Model ?? model)
                .ToDictionary(model => model.Id, StringComparer.Ordinal);
        }
        bool SameConnection(ProviderProfile profile) => discoveryConnection is not null &&
            profile.DefinitionId == discoveryConnection.DefinitionId &&
            profile.Credentials.Count == discoveryConnection.Credentials.Count &&
            profile.Credentials.All(item => discoveryConnection.Credentials.TryGetValue(item.Key, out var value) && value == item.Value);
        DiscoveredModels? CurrentDiscovery() => SameConnection(Connection()) ? discovery : null;
        bool IsDecision(string apiId) => editingClassifier || classificationIds.Contains(apiId) ||
            Connection().Definition.BuiltInDecisionModels.Any(model => model.Id == apiId) ||
            CurrentDiscovery()?.DecisionIds.Contains(apiId, StringComparer.Ordinal) == true;
        void Capture()
        {
            if (currentId is null || IsDecision(currentId) || LakeCode.Core.Agent.ModelCatalog.GetModelType(currentId) == LakeCode.Core.Agent.ModelType.ImageGeneration) return;
            drafts[currentId] = modelEditor.Capture();
            var row = modelList.Items.FirstOrDefault(item => item.Tag as string == currentId);
            if (row is not null) row.Text = ModelCaption(currentId);
        }
        string ModelCaption(string apiId)
        {
            if (IsDecision(apiId))
                return Markup.Escape($"{apiId} · Классификация" +
                    (Connection().Definition.CanSelectDecisionModel(apiId) ? " · доступна для выбора" : " · недоступна для выбора") +
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
            var decisionModel = currentId is not null && IsDecision(currentId);
            modelEditor.Control.Visible = currentId is not null && !decisionModel && !imageModel;
            removeManual.IsEnabled = currentId is not null && (decisionModel
                ? classificationIds.Contains(currentId) : CurrentDiscovery()?.Ids.Contains(currentId, StringComparer.Ordinal) != true);
            reset.Visible = !editingClassifier;
            reset.IsEnabled = currentId is not null && !decisionModel && !imageModel;
            selectedLabel.Text = currentId is null ? "" : Markup.Escape("API ID: " + currentId);
            if (imageModel)
            {
                selectedLabel.Text = Markup.Escape($"API ID: {currentId} · Генерация изображений. Назначение: /models → Изображения. Чат-характеристики не требуются.");
                return;
            }
            if (currentId is null || decisionModel) return;
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
            var connection = Connection() with { Discovery = visibleDiscovery };
            IEnumerable<string> catalogIds = connection.Definition.ModelIds(connection)
                .Concat(connection.Definition.BuiltInDecisionModels.Select(model => model.Id));
            IEnumerable<string> manualIds = editingClassifier ? classificationIds : drafts.Keys.Concat(classificationIds)
                .Where(id => connection.Definition.IsModelAllowed(connection, id) || IsDecision(id));
            foreach (var apiId in catalogIds.Concat(visibleDiscovery?.Ids ?? []).Concat(visibleDiscovery?.DecisionIds ?? []).Concat(manualIds)
                         .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                modelList.AddItem(new ListItem(ModelCaption(apiId)) { Tag = apiId });
                if (apiId == selected) modelList.SelectedIndex = modelList.Items.Count - 1;
            }
            if (modelList.Items.Count > 0 && modelList.SelectedIndex < 0) modelList.SelectedIndex = 0;
            if (modelList.Items.Count == 0) modelList.AddItem(new ListItem("Обновите список или добавьте API ID вручную."));
            discoveryStatus.Text = editingClassifier
                ? Markup.Escape(original.Definition.DecisionModelsHint ?? "Добавьте или обновите список моделей решений.")
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
                var result = await connection.DiscoverAsync(lifetime.Token).ConfigureAwait(false);
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
                if (!connection.HasCredentials) throw new InvalidDataException("Настройте авторизацию.");
            }
            catch (Exception error) { tabs.ActiveTabIndex = 0; Error(error.Message); return; }
            var rules = ImmutableDictionary.CreateBuilder<string, ModelOverride>(StringComparer.Ordinal);
            foreach (var (apiId, draft) in editingClassifier ? new Dictionary<string, ProviderModelEditor.Draft>() : drafts)
            {
                try
                {
                    if (draft.ToRule() is not { } rule) continue;
                    if (!connection.Definition.IsModelAllowed(connection, apiId))
                    {
                        rules[apiId] = rule;
                        continue;
                    }
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
            foreach (var id in classificationIds) rules[id] = new ModelOverride { Manual = true, Decision = true };
            var profile = connection with
            {
                ModelOverrides = rules.ToImmutable(), Discovery = SameConnection(connection) ? discovery : null
            };
            Busy(true, writing: true);
            status.Text = "[grey70]Сохранение…[/]";
            var refreshImported = SelectedMethod().RequiresImport &&
                (originalMethod != SelectedMethod().Id || !ReferenceEquals(imported, original));
            try
            {
                await Task.Run(() => providers.SaveAsync(profile, lifetime.Token), lifetime.Token).ConfigureAwait(false);
                string? discoveryError = null;
                if (refreshImported)
                {
                    try { await providers.RefreshDiscoveryAsync(profile.Id, lifetime.Token).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { discoveryError = error is InvalidDataException ? error.Message : "Не удалось получить список моделей."; }
                }
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    models.Refresh();
                    changed();
                    if (discoveryError is null) system.CloseWindow(window);
                    else
                    {
                        Busy(false);
                        tabs.IsEnabled = save.IsEnabled = false;
                        cancel.Text = "Закрыть";
                        status.Text = $"[yellow]Подключение сохранено. {Markup.Escape(discoveryError)}[/]";
                    }
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
