using System.Collections.Immutable;
using System.Globalization;
using LakeCode.Cli.State;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;

namespace LakeCode.Cli.Components;

/// <summary>Edits user-owned connection profiles; never adds credentials to the chat draft.</summary>
internal sealed class ProvidersWindowComponent(ProviderRegistry providers, ModelStore models, CancellationToken shutdown)
{
    private static readonly Color Surface = new(30, 36, 39);

    public void Show(ConsoleWindowSystem system)
    {
        var list = Choices(8);
        list.AddItem(new ListItem("[cyan]Добавить подключение…[/]"));
        foreach (var profile in providers.Profiles)
            list.AddItem(new ListItem(Markup.Escape($"{profile.Name} · {profile.Kind}")) { Tag = profile.Id });
        var hint = Label("[grey70]Enter — изменить · Esc — закрыть\nКлючи хранятся в ~/.lakecode/providers.json без шифрования, с приватными правами на Linux.[/]");
        var window = Base(system, "Провайдеры", 76, Math.Min(18, list.Items.Count + 7))
            .AddControl(list).AddControl(hint).Build();
        list.ItemActivated += (_, _) =>
        {
            var profile = list.SelectedItem?.Tag is string id ? providers.Get(id) : null;
            system.CloseWindow(window);
            Edit(system, profile);
        };
        system.AddWindow(window);
        window.FocusControl(list);
    }

    private void Edit(ConsoleWindowSystem system, ProviderProfile? original)
    {
        var name = Field("Название: ", original?.Name ?? "");
        var url = Field("Base URL: ", original?.BaseUrl ?? "");
        var key = Field("API key: ", "");
        key.MaskCharacter = '*';
        key.MaxHistoryEntries = 0;
        key.Placeholder = original is null ? "Введите ключ" : "Пустое поле — сохранить прежний ключ";
        var protocol = Choices(3);
        protocol.AddItem(new ListItem("OpenAI · Responses") { Tag = (ProviderKind.OpenAI, OpenAIApiMode.Responses) });
        protocol.AddItem(new ListItem("OpenAI · Chat Completions") { Tag = (ProviderKind.OpenAI, OpenAIApiMode.ChatCompletions) });
        protocol.AddItem(new ListItem("Anthropic · Messages") { Tag = (ProviderKind.Anthropic, OpenAIApiMode.Responses) });
        protocol.SelectedIndex = original?.Kind == ProviderKind.Anthropic ? 2 : original?.ApiMode == OpenAIApiMode.ChatCompletions ? 1 : 0;
        var modelText = Field("", "");
        modelText.Multiline = true;
        modelText.Input = original is null ? "" : FormatModels(original.Models);
        modelText.EnterBehavior = EnterBehavior.InsertNewline;
        modelText.MinRows = 4;
        modelText.MaxRows = 6;
        modelText.MaxLength = 32000;
        modelText.Placeholder = "Добавьте модели: одна строка на модель, формат указан выше";
        var status = Label("");
        var save = Button("Сохранить");
        var delete = Button("Удалить подключение");
        delete.Visible = original is not null;
        var busy = false;
        var form = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill,
            BorderStyle = BorderStyle.None, ShowScrollbar = true, AutoScroll = true
        };
        form.AddControl(name);
        form.AddControl(Label("[grey70]Протокол (↑↓):[/]"));
        form.AddControl(protocol);
        form.AddControl(url);
        form.AddControl(Label("[grey70]OpenAI: URL с /v1; Anthropic: корень URL (SDK добавляет /v1/messages).\nHTTP не шифрует ключ и сообщения — используйте только для доверенного локального сервера.[/]"));
        form.AddControl(key);
        form.AddControl(Label("[cyan]Модели[/]\n[grey70]id | имя | context | max input | max output | image: yes/no | reasoning через запятую\nOpenAI: none,low,medium,high,xhigh,max · Anthropic: none,budget:N (1024 ≤ N < max output).\nЛимиты задавайте по документации модели; они не определяются автоматически.[/]"));
        form.AddControl(modelText);
        var window = Base(system, original is null ? "Новое подключение" : "Настройки подключения", 96, 30)
            .AddControl(form).AddControl(status).AddControl(save).AddControl(delete)
            .AddControl(Label("[grey70]Tab — следующее поле · Ctrl+S — сохранить · Esc — отменить.\nИзменения применяются к новым отправкам; принятые сообщения сохраняют своё подключение.[/]"))
            .Build();
        window.OnClosing += (_, _) => { key.Input = ""; key.ClearHistory(); };
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key == ConsoleKey.Escape && busy) e.Handled = true;
            if (e.KeyInfo.Key == ConsoleKey.S && e.KeyInfo.Modifiers == ConsoleModifiers.Control)
            { e.Handled = true; _ = SaveAsync(); }
        };
        save.Click += (_, _) => _ = SaveAsync();
        delete.Click += (_, _) =>
        {
            if (!busy && original is not null) ConfirmDelete(system, window, original, status);
        };
        system.AddWindow(window);
        window.FocusControl(name);

        async Task SaveAsync()
        {
            if (busy) return;
            ProviderProfile profile;
            try
            {
                var choice = ((ProviderKind, OpenAIApiMode))protocol.SelectedItem!.Tag!;
                profile = new ProviderProfile
                {
                    Id = original?.Id ?? Guid.NewGuid().ToString("D"), Name = name.Input.Trim(),
                    Kind = choice.Item1, ApiMode = choice.Item2, BaseUrl = url.Input.Trim().TrimEnd('/'),
                    ApiKey = string.IsNullOrEmpty(key.Input) ? original?.ApiKey ?? "" : key.Input,
                    Models = ParseModels(modelText.Input)
                };
                profile.Validate();
                if (string.IsNullOrWhiteSpace(profile.ApiKey)) throw new InvalidDataException("Введите API-ключ.");
            }
            catch (Exception error) { status.Text = $"[yellow]{Markup.Escape(error.Message)}[/]"; return; }
            busy = true;
            status.Text = "[cyan]Сохранение…[/]";
            try
            {
                await providers.SaveAsync(profile, shutdown).ConfigureAwait(false);
                system.EnqueueOnUIThread(() =>
                {
                    models.Refresh();
                    key.Input = "";
                    key.ClearHistory();
                    system.CloseWindow(window);
                });
            }
            catch (Exception)
            {
                // Storage exceptions may contain user data; do not expose credential-bearing details.
                system.EnqueueOnUIThread(() => { busy = false; status.Text = "[yellow]Не удалось сохранить настройки. Проверьте доступ к ~/.lakecode/providers.json; поля сохранены.[/]"; });
            }
        }
    }

    private void ConfirmDelete(ConsoleWindowSystem system, Window editor, ProviderProfile profile, MarkupControl status)
    {
        var choices = Choices(2);
        choices.AddItem(new ListItem("Отмена"));
        choices.AddItem(new ListItem("Удалить подключение"));
        choices.SelectedIndex = 0;
        var busy = false;
        var window = Base(system, "Удаление подключения", 68, 10)
            .AddControl(Label($"{Markup.Escape(profile.Name)}\n[grey70]История диалогов останется. После перезапуска ожидающие\nсообщения этого подключения не смогут выполняться.[/]"))
            .AddControl(choices).Build();
        choices.ItemActivated += async (_, _) =>
        {
            if (busy) return;
            if (choices.SelectedIndex == 0) { system.CloseWindow(window); return; }
            busy = true;
            try
            {
                await providers.DeleteAsync(profile.Id, shutdown).ConfigureAwait(false);
                system.EnqueueOnUIThread(() => { models.Refresh(); system.CloseWindow(window); system.CloseWindow(editor); });
            }
            catch (Exception)
            {
                system.EnqueueOnUIThread(() => { system.CloseWindow(window); status.Text = "[yellow]Не удалось удалить подключение. Проверьте доступ к файлу настроек.[/]"; });
            }
        };
        window.PreviewKeyPressed += (_, e) => { if (busy && e.KeyInfo.Key == ConsoleKey.Escape) e.Handled = true; };
        system.AddWindow(window);
        window.FocusControl(choices);
    }

    private static string FormatModels(ImmutableArray<ModelInfo> values) => string.Join("\n", values.Select(model =>
        $"{model.Id} | {model.Name} | {model.ContextWindowTokens} | {model.MaxInputTokens} | {model.MaxOutputTokens} | {(model.InputModalities.Contains(ModelModality.Image) ? "yes" : "no")} | {string.Join(',', model.ReasoningEfforts)}"));

    private static ImmutableArray<ModelInfo> ParseModels(string input)
    {
        var values = ImmutableArray.CreateBuilder<ModelInfo>();
        foreach (var line in input.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var parts = line.Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length != 7 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var context) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var maxInput) ||
                !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var maxOutput) || parts[5] is not ("yes" or "no"))
                throw new InvalidDataException($"Некорректная строка модели №{values.Count + 1}. Нужны 7 полей; лимиты — целые числа, image — yes/no.");
            values.Add(new ModelInfo(parts[0], parts[1], context, maxInput, maxOutput,
                parts[5] == "yes" ? [ModelModality.Text, ModelModality.Image] : [ModelModality.Text], [ModelModality.Text],
                parts[6].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray()));
        }
        return values.ToImmutable();
    }

    private static PromptControl Field(string prompt, string value) => new()
    {
        Prompt = prompt, Input = value, UnfocusOnEnter = false, HorizontalAlignment = HorizontalAlignment.Stretch,
        InputBackgroundColor = Color.Transparent, InputFocusedBackgroundColor = Color.Transparent,
        Margin = new Margin(2, 0, 2, 0), MaxLength = 8192
    };
    private static MarkupControl Label(string text) => new(new List<string> { text }) { Wrap = true, Margin = new Margin(2, 0, 2, 0) };
    private static ButtonControl Button(string text) => new()
    {
        Text = text, BackgroundColor = Color.Transparent, FocusedBackgroundColor = new Color(35, 65, 75), Margin = new Margin(2, 0, 2, 0)
    };
    private static ListControl Choices(int count) => new()
    {
        IsSelectable = true, MaxVisibleItems = count, BackgroundColor = Surface, ForegroundColor = Color.White,
        HighlightBackgroundColor = new Color(35, 65, 75), HighlightForegroundColor = Color.White,
        HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Margin(2, 0, 2, 0)
    };
    private static WindowBuilder Base(ConsoleWindowSystem system, string title, int width, int height) => new WindowBuilder(system)
        .WithTitle(title).WithSize(width, height).Centered().AsModal().HideTitleButtons()
        .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
        .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
        .WithBackgroundColor(Surface).WithForegroundColor(Color.White)
        .OnKeyPressed((sender, e) =>
        {
            if (!e.Handled && e.KeyInfo.Key == ConsoleKey.Escape) { system.CloseWindow((Window)sender!); e.Handled = true; }
        });
}
