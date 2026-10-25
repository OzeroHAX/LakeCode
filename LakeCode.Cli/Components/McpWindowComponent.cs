using System.Collections.Immutable;
using System.Text.Json;
using LakeCode.Core.Mcp;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;
using static LakeCode.Cli.Components.ProviderWindowControls;

namespace LakeCode.Cli.Components;

/// <summary>Connection drafts only. Saving never starts a process or connects to a server.</summary>
internal sealed class McpWindowComponent(McpSettingsStorage storage, CancellationToken shutdown)
{
    public void Show(ConsoleWindowSystem system)
    {
        var list = Choices(10);
        list.VerticalAlignment = VerticalAlignment.Fill;
        var add = Button("Добавить");
        var edit = Button("Изменить");
        var toggle = Button("Включить / отключить");
        var delete = Button("Удалить");
        var close = Button("Закрыть");
        var status = Label("");
        var busy = false;
        var closed = false;
        var window = Base(system, "MCP-серверы", 90, 24)
            .AddControl(Label("MCP доступен только в Build. Поиск запускает серверы и раскрывает только найденные инструменты."))
            .AddControl(list).AddControl(Buttons(add, edit, toggle, delete)).AddControl(status)
            .AddControl(Label("[grey70]~/.lakecode/mcp.json · headers/env хранятся без шифрования. Новые настройки применяются к следующим сообщениям; принятая очередь сохраняет свой снимок.[/]"))
            .AddControl(Buttons(close)).Build();
        window.OnClosing += (_, _) => closed = true;
        close.Click += (_, _) => { if (!busy) system.CloseWindow(window); };
        window.PreviewKeyPressed += (_, e) =>
        { if (e.KeyInfo.Key == ConsoleKey.Escape) { e.Handled = true; if (!busy) system.CloseWindow(window); } };
        add.Click += (_, _) => Edit(system, null, Refresh);
        edit.Click += (_, _) => { if (Selected() is { } profile) Edit(system, profile, Refresh); };
        list.ItemActivated += (_, _) => { if (!busy && Selected() is { } profile) Edit(system, profile, Refresh); };
        toggle.Click += (_, _) => { if (Selected() is { } profile) _ = ToggleAsync(profile); };
        delete.Click += (_, _) => { if (Selected() is { } profile) ConfirmDelete(system, profile, Refresh); };
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        Refresh();
        system.AddWindow(window);
        window.FocusControl(list);

        McpServerProfile? Selected() => list.SelectedItem?.Tag is string id ? storage.Servers.FirstOrDefault(s => s.Id == id) : null;
        void UpdateButtons()
        {
            edit.IsEnabled = toggle.IsEnabled = delete.IsEnabled = !busy && Selected() is not null;
            add.IsEnabled = close.IsEnabled = !busy;
        }
        void Refresh()
        {
            var selected = list.SelectedItem?.Tag as string;
            list.ClearItems();
            foreach (var server in storage.Servers.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                list.AddItem(new ListItem(Markup.Escape($"{server.Name} · {server.Transport} · {(server.Enabled ? "включён" : "отключён")} · {(server.Approval == McpApprovalPolicy.Ask ? "подтверждать каждый вызов" : "вызовы без подтверждения")}")) { Tag = server.Id });
                if (server.Id == selected) list.SelectedIndex = list.Items.Count - 1;
            }
            if (list.Items.Count == 0) list.AddItem(new ListItem("Нет серверов. Нажмите «Добавить»."));
            UpdateButtons();
        }
        async Task ToggleAsync(McpServerProfile profile)
        {
            if (busy) return;
            busy = true; UpdateButtons();
            try
            {
                await Task.Run(() => storage.SaveServerAsync(profile with { Enabled = !profile.Enabled }, shutdown), shutdown).ConfigureAwait(false);
                system.EnqueueOnUIThread(() => { if (closed) return; busy = false; status.Text = ""; Refresh(); });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() => { if (closed) return; busy = false; UpdateButtons(); status.Text = "[yellow]Не удалось сохранить. Проверьте доступ и права 600 на mcp.json.[/]"; });
            }
        }
    }

    private void Edit(ConsoleWindowSystem system, McpServerProfile? original, Action changed)
    {
        var name = Field("Название: ", original?.Name ?? "");
        var transport = Dropdown("Транспорт: ", "stdio — локальный процесс", "Streamable HTTP — удалённый сервер");
        transport.SelectedIndex = original?.Transport == McpTransport.Http ? 1 : 0;
        var enabled = Dropdown("Состояние: ", "Включён", "Отключён");
        enabled.SelectedIndex = original?.Enabled == false ? 1 : 0;
        var policy = Dropdown("Подтверждение: ", "Каждый вызов вручную", "Разрешить все вызовы этого сервера");
        policy.SelectedIndex = original?.Approval == McpApprovalPolicy.Allow ? 1 : 0;
        var command = Field("Команда: ", original?.Command ?? "");
        var arguments = Field("Аргументы JSON: ", JsonSerializer.Serialize(original?.Arguments ?? []));
        var directory = Field("Рабочая папка: ", original?.WorkingDirectory ?? "");
        directory.Placeholder = "Пусто — корень проекта; иначе абсолютный путь";
        var url = Field("URL MCP: ", original?.Url ?? "");
        var headers = Secret("Headers JSON: ");
        var environment = Secret("Env JSON: ");
        var form = Form();
        foreach (var control in new IWindowControl[] { name, transport, enabled, policy,
            Label("[yellow]Внешние серверы не изолированы файловыми ограничениями LakeCode. Разрешение всех вызовов включает операции записи и удаления. HTTP без TLS не защищает headers и данные.[/]"),
            command, arguments, directory, url, headers, environment,
            Label(Markup.Escape("Аргументы: [\"arg1\",\"arg2\"]. Headers/env: JSON-объекты строк. Пустое секретное поле сохраняет прежние значения; {} удаляет их. OAuth и legacy SSE не поддерживаются.")),
            Label("[grey70]stdio получает стандартные системные переменные и явно заданный env, не всё окружение LakeCode. Сохранение не запускает сервер.[/]") }) form.AddControl(control);
        var save = Button("Сохранить");
        var cancel = Button("Отмена");
        var status = Label("");
        var saving = false;
        var closed = false;
        var window = Base(system, original is null ? "Новый MCP-сервер" : "Настройки MCP-сервера", 94, 32)
            .AddControl(form).AddControl(status).AddControl(Buttons(cancel, save))
            .AddControl(Label("[grey70]Tab — поля · Enter — действие · Esc — отмена[/]")).Build();
        transport.SelectedIndexChanged += (_, _) => Visibility();
        Visibility();
        window.OnClosing += (_, _) => { closed = true; headers.Input = environment.Input = ""; headers.ClearHistory(); environment.ClearHistory(); };
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.Escape || transport.IsDropdownOpen || enabled.IsDropdownOpen || policy.IsDropdownOpen) return;
            e.Handled = true;
            if (!saving) system.CloseWindow(window);
        };
        cancel.Click += (_, _) => { if (!saving) system.CloseWindow(window); };
        save.Click += (_, _) => _ = SaveAsync();
        system.AddWindow(window);
        window.FocusControl(name);

        void Visibility()
        {
            var stdio = transport.SelectedIndex == 0;
            command.Visible = arguments.Visible = directory.Visible = environment.Visible = stdio;
            url.Visible = headers.Visible = !stdio;
        }
        PromptControl Secret(string caption)
        {
            var field = Field(caption);
            field.MaskCharacter = '*';
            field.MaxHistoryEntries = 0;
            field.Placeholder = original is null ? "JSON-объект или пусто" : "Пусто — сохранить; {} — очистить";
            return field;
        }
        ImmutableDictionary<string, string> Values(PromptControl field, ImmutableDictionary<string, string>? previous)
        {
            if (field.Input.Length == 0) return previous ?? ImmutableDictionary<string, string>.Empty;
            return JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(field.Input) ?? throw new JsonException();
        }
        async Task SaveAsync()
        {
            if (saving) return;
            McpServerProfile profile;
            try
            {
                profile = (original ?? new McpServerProfile()) with
                {
                    Name = name.Input.Trim(), Enabled = enabled.SelectedIndex == 0,
                    Transport = transport.SelectedIndex == 0 ? McpTransport.Stdio : McpTransport.Http,
                    Approval = policy.SelectedIndex == 0 ? McpApprovalPolicy.Ask : McpApprovalPolicy.Allow,
                    Command = command.Input.Trim(), Arguments = JsonSerializer.Deserialize<ImmutableArray<string>>(arguments.Input),
                    WorkingDirectory = directory.Input.Trim(), Url = url.Input.Trim(),
                    Headers = Values(headers, original?.Headers), Environment = Values(environment, original?.Environment)
                };
                profile.Validate();
            }
            catch (JsonException) { status.Text = "[yellow]Проверьте JSON: аргументы — массив строк; headers/env — объекты строк. Черновик сохранён.[/]"; return; }
            catch (InvalidDataException error) { status.Text = "[yellow]" + Markup.Escape(error.Message) + "[/]"; return; }
            saving = true;
            form.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            status.Text = "[grey70]Сохранение…[/]";
            try
            {
                await Task.Run(() => storage.SaveServerAsync(profile, shutdown), shutdown).ConfigureAwait(false);
                system.EnqueueOnUIThread(() => { if (closed) return; changed(); system.CloseWindow(window); });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    saving = false;
                    form.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
                    status.Text = "[yellow]Не удалось сохранить. Проверьте доступ и права 600 на mcp.json; черновик сохранён.[/]";
                });
            }
        }
    }

    private void ConfirmDelete(ConsoleWindowSystem system, McpServerProfile profile, Action changed)
    {
        var cancel = Button("Отмена");
        var delete = Button("Удалить");
        var status = Label("");
        var busy = false;
        var closed = false;
        var window = Base(system, "Удаление MCP-сервера", 76, 13)
            .AddControl(Label(Markup.Escape(profile.Name)))
            .AddControl(Label("Следующие сообщения не смогут использовать этот сервер. Принятые сообщения сохраняют свой снимок настроек; восстановленная очередь с прежней конфигурацией потребует повторной отправки."))
            .AddControl(status).AddControl(Buttons(cancel, delete)).Build();
        window.OnClosing += (_, _) => closed = true;
        cancel.Click += (_, _) => { if (!busy) system.CloseWindow(window); };
        window.PreviewKeyPressed += (_, e) => { if (e.KeyInfo.Key == ConsoleKey.Escape) { e.Handled = true; if (!busy) system.CloseWindow(window); } };
        delete.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; cancel.IsEnabled = delete.IsEnabled = false;
            try
            {
                await Task.Run(() => storage.DeleteServerAsync(profile.Id, shutdown), shutdown).ConfigureAwait(false);
                system.EnqueueOnUIThread(() => { if (closed) return; changed(); system.CloseWindow(window); });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() =>
                {
                    if (closed) return;
                    busy = false; cancel.IsEnabled = delete.IsEnabled = true;
                    status.Text = "[yellow]Не удалось удалить. Проверьте доступ к mcp.json.[/]";
                });
            }
        };
        system.AddWindow(window);
        window.FocusControl(cancel);
    }
}
