using System.Collections.Immutable;
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
        add.Click += (_, _) => Edit(system, null, Refresh);
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
                var protocol = profile.Kind switch { ProviderKind.TypeSafe => "TypeSafe · System One", ProviderKind.Anthropic => "Anthropic · Messages", _ => $"OpenAI · {profile.ApiMode}" };
                var total = profile.Kind == ProviderKind.TypeSafe ? profile.ClassificationModels.Length : profile.ModelEntries.Length;
                var ready = profile.Kind == ProviderKind.TypeSafe ? profile.ClassificationModels.Count(m => m.IsVersioned) : profile.Models.Length;
                list.AddItem(new ListItem(Markup.Escape($"{profile.Name} · {protocol} · готово {ready}, всего {total}")) { Tag = profile.Id });
                if (profile.Id == selected) list.SelectedIndex = list.Items.Count - 1;
            }
            if (list.Items.Count == 0) list.AddItem(new ListItem("Нет подключений. Нажмите «Добавить»."));
            edit.IsEnabled = delete.IsEnabled = Selected() is not null;
            status.Text = "[grey70]Подключения и ключи — ~/.lakecode/providers.json. Ключи хранятся без шифрования.[/]";
        }
    }

    private void Edit(ConsoleWindowSystem system, ProviderProfile? original, Action changed)
    {
        var id = original?.Id ?? Guid.NewGuid().ToString("D");
        var name = Field("Название: ", original?.Name ?? "");
        var protocol = Dropdown("Протокол: ", "OpenAI · Responses", "OpenAI · Chat Completions", "Anthropic · Messages", "TypeSafe · System One");
        protocol.SelectedIndex = original?.Kind == ProviderKind.TypeSafe ? 3 : original?.Kind == ProviderKind.Anthropic ? 2 : original?.ApiMode == OpenAIApiMode.ChatCompletions ? 1 : 0;
        var url = Field("Base URL: ", original?.BaseUrl ?? "");
        var urlHint = Label("");
        var key = Field("API key: ");
        key.MaskCharacter = '*';
        key.MaxHistoryEntries = 0;
        key.Placeholder = original is null ? "Введите ключ" : "Пустое поле — сохранить текущий ключ";
        var connectionForm = Form();
        connectionForm.AddControl(name);
        connectionForm.AddControl(protocol);
        connectionForm.AddControl(url);
        connectionForm.AddControl(urlHint);
        connectionForm.AddControl(key);
        connectionForm.AddControl(Label("[grey70]Ключ хранится локально без шифрования. HTTP не шифрует ключ и сообщения.[/]"));
        protocol.SelectedIndexChanged += (_, _) => UpdateUrlHint();
        UpdateUrlHint();

        var discovery = original?.Discovery;
        ProviderProfile? discoveryConnection = original;
        var drafts = (original?.ModelOverrides ?? ImmutableDictionary<string, ModelOverride>.Empty)
            .ToDictionary(item => item.Key, item => ProviderModelEditor.Draft.FromRule(item.Value), StringComparer.Ordinal);
        var classificationIds = (original?.ManualClassificationModels ?? []).ToHashSet(StringComparer.Ordinal);
        var editingClassifier = protocol.SelectedIndex == 3;
        var modelList = Choices(5);
        var modelEditor = new ProviderModelEditor();
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
        var window = Base(system, original is null ? "Новый провайдер" : "Настройки провайдера", 94, 32)
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
            if (protocol.IsDropdownOpen || modelEditor.DropdownOpen) return;
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
        tabs.TabChanged += (_, _) => { Capture(); RefreshModels(); };
        protocol.SelectedIndexChanged += (_, _) =>
        {
            Capture();
            editingClassifier = protocol.SelectedIndex == 3;
            currentId = null;
            RefreshModels();
        };
        RefreshModels();
        system.AddWindow(window);
        window.FocusControl(name);

        void UpdateUrlHint()
        {
            urlHint.Text = protocol.SelectedIndex switch
            {
                3 => "[grey70]TypeSafe: https://api.typesafe.ai/v1 · discovery /models, классификация /systemone.[/]",
                2 => "[grey70]Корень URL без /v1: SDK добавляет /v1/messages, discovery — /v1/models.[/]",
                _ => "[grey70]Базовый URL OpenAI API (обычно с /v1). Discovery использует /models.[/]"
            };
            url.Placeholder = protocol.SelectedIndex == 3 ? "https://api.typesafe.ai/v1" : "";
        }

        ProviderProfile Connection() => new()
        {
            Id = id, Name = name.Input.Trim(), Kind = protocol.SelectedIndex switch { 3 => ProviderKind.TypeSafe, 2 => ProviderKind.Anthropic, _ => ProviderKind.OpenAI },
            ApiMode = protocol.SelectedIndex == 1 ? OpenAIApiMode.ChatCompletions : OpenAIApiMode.Responses,
            BaseUrl = url.Input.Trim().TrimEnd('/'), ApiKey = key.Input.Length == 0 ? original?.ApiKey ?? "" : key.Input
        };
        bool SameConnection(ProviderProfile profile) => discoveryConnection is not null && profile.Kind == discoveryConnection.Kind &&
            profile.BaseUrl.TrimEnd('/') == discoveryConnection.BaseUrl.TrimEnd('/') && profile.ApiKey == discoveryConnection.ApiKey;
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
                text += entry.Error is not null ? " · для чата нужны характеристики" : " · " + entry.Source;
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
            IEnumerable<string> manualIds = editingClassifier ? classificationIds : drafts.Keys;
            foreach (var apiId in (visibleDiscovery?.Ids ?? []).Concat(manualIds).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                modelList.AddItem(new ListItem(ModelCaption(apiId)) { Tag = apiId });
                if (apiId == selected) modelList.SelectedIndex = modelList.Items.Count - 1;
            }
            if (modelList.Items.Count > 0 && modelList.SelectedIndex < 0) modelList.SelectedIndex = 0;
            if (modelList.Items.Count == 0) modelList.AddItem(new ListItem("Обновите список или добавьте API ID вручную."));
            discoveryStatus.Text = editingClassifier
                ? "[grey70]System One: discovery может вернуть только алиасы. Для классификатора добавьте фиксированный ID, например jev-1.13.0. Reasoning и чат-параметры не нужны.[/]"
                : Markup.Escape(ChatModelPicker.DiscoveryText(Connection() with { Discovery = visibleDiscovery }));
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
