using LakeCode.Cli.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;

namespace LakeCode.Cli.Components;

internal sealed class SessionsWindowComponent
{
    public void Show(ConsoleWindowSystem windowSystem, IReadOnlyList<SessionSummary> sessions,
        IReadOnlyList<string> errors, string? activeId, Action<string> openSession)
    {
        var surface = new Color(30, 36, 39);
        var list = new ListControl
        {
            IsSelectable = true,
            BackgroundColor = surface,
            FocusedBackgroundColor = surface,
            ForegroundColor = Color.White,
            HighlightBackgroundColor = new Color(35, 65, 75),
            HighlightForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            Margin = new Margin(2, 1, 2, 0)
        };
        foreach (var session in sessions)
        {
            var title = string.Join(" ", session.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length > 34) title = title[..34] + "…";
            list.AddItem(new ListItem($"{Markup.Escape(title)}{(session.Id == activeId ? " [cyan]*[/]" : "")}  " +
                $"[grey70]{session.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {session.Turns}[/]") { Tag = session.Id });
        }
        if (sessions.Count > 0) list.SelectedIndex = 0;
        var hint = new MarkupControl(new List<string>
        {
            sessions.Count == 0 ? "[grey70]В этом проекте пока нет сохранённых сессий.[/]" :
                "[grey70]↑↓ выбор · Enter открыть · Esc отмена[/]",
            "[grey70]Прерванные действия автоматически не повторяются.[/]"
        }) { BackgroundColor = surface, Margin = new Margin(2, 1, 2, 0), Wrap = true };
        if (errors.Count > 0)
            hint.SetContent(["[yellow]Некоторые сессии не удалось прочитать:[/]",
                Markup.Escape(string.Join("\n", errors.Take(3))), "[grey70]Enter открыть · Esc отмена[/]"]);
        var window = new WindowBuilder(windowSystem)
            .WithTitle("Сессии текущего проекта")
            .WithSize(76, Math.Clamp(sessions.Count + 7, 10, 24))
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded)
            .WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(surface).WithForegroundColor(Color.White)
            .AddControl(list).AddControl(hint)
            .OnKeyPressed((sender, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;
                windowSystem.CloseWindow((Window)sender!);
                e.Handled = true;
            }).Build();
        list.ItemActivated += (_, _) =>
        {
            if (list.SelectedItem?.Tag is not string id) return;
            windowSystem.CloseWindow(window);
            openSession(id);
        };
        windowSystem.AddWindow(window);
        window.FocusControl(list);
    }
}
