using LakeCode.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Markup = Spectre.Console.Markup;

namespace LakeCode.Cli.Components;

internal sealed class SessionsWindowComponent
{
    private const int PageSize = 100;
    private IReadOnlyList<SessionSummary> _sessions = Array.Empty<SessionSummary>();
    private IReadOnlyList<string> _errors = Array.Empty<string>();
    private Window? _window;
    private ListControl? _list;
    private MarkupControl? _hint;
    private string? _activeId;
    private string? _loadError;
    private bool _loading;
    private int _page;

    public void ShowLoading(ConsoleWindowSystem windowSystem, string? activeId, Action<string> openSession)
    {
        if (_window is not null) return;
        _activeId = activeId;
        _loading = true;
        _loadError = null;
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
        var hint = new MarkupControl(new List<string>())
        {
            BackgroundColor = surface, Margin = new Margin(2, 1, 2, 0), Wrap = true
        };
        var window = new WindowBuilder(windowSystem)
            .WithTitle("Сессии текущего проекта")
            .WithSize(76, 24)
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded)
            .WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(surface).WithForegroundColor(Color.White)
            .AddControl(list).AddControl(hint)
            .OnKeyPressed((sender, e) =>
            {
                if (e.KeyInfo.Key == ConsoleKey.Escape)
                {
                    windowSystem.CloseWindow((Window)sender!);
                    e.Handled = true;
                }
                else if (e.KeyInfo.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
                {
                    var next = _page + (e.KeyInfo.Key == ConsoleKey.RightArrow ? 1 : -1);
                    if (next >= 0 && next <= Math.Max(0, (_sessions.Count - 1) / PageSize))
                    {
                        _page = next;
                        Render();
                    }
                    e.Handled = true;
                }
            })
            .OnClosed((_, _) =>
            {
                _window = null;
                _list = null;
                _hint = null;
            }).Build();
        list.ItemActivated += (_, _) =>
        {
            // The service is busy while refreshing; cached rows are for browsing only.
            if (_loading || list.SelectedItem?.Tag is not string id) return;
            windowSystem.CloseWindow(window);
            openSession(id);
        };
        _window = window;
        _list = list;
        _hint = hint;
        Render();
        windowSystem.AddWindow(window);
        window.FocusControl(list);
    }

    public void Update(IReadOnlyList<SessionSummary> sessions, IReadOnlyList<string> errors, string? activeId)
    {
        var selectedId = _list?.SelectedItem?.Tag as string;
        _sessions = sessions;
        _errors = errors;
        _activeId = activeId;
        _loading = false;
        _loadError = null;
        var selectedIndex = -1;
        if (selectedId is not null)
        {
            for (var index = 0; index < sessions.Count; index++)
            {
                if (sessions[index].Id != selectedId) continue;
                selectedIndex = index;
                break;
            }
        }
        _page = selectedIndex >= 0 ? selectedIndex / PageSize :
            Math.Min(_page, Math.Max(0, (sessions.Count - 1) / PageSize));
        Render(selectedIndex >= 0 ? selectedIndex % PageSize : 0);
        // A result received after Esc updates the cache, but never reopens the window.
    }

    public void ShowError(string error)
    {
        _loading = false;
        _loadError = error;
        Render(_list?.SelectedIndex ?? 0);
    }

    private void Render(int selectedIndex = 0)
    {
        if (_list is null || _hint is null) return;
        var items = new List<ListItem>();
        foreach (var session in _sessions.Skip(_page * PageSize).Take(PageSize))
        {
            var title = string.Join(" ", session.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length > 34) title = title[..34] + "…";
            items.Add(new ListItem($"{Markup.Escape(title)}{(session.Id == _activeId ? " [cyan]*[/]" : "")}  " +
                $"[grey70]{session.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {session.Turns}[/]") { Tag = session.Id });
        }
        _list.SelectedIndex = -1;
        _list.Items = items;
        _list.SelectedIndex = items.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, items.Count - 1);
        var lines = new List<string>();
        if (_loading)
            lines.Add(_sessions.Count == 0 ? "[cyan]Загрузка сессий…[/]" :
                "[cyan]Обновление списка… Показан предыдущий список.[/]");
        else if (_loadError is not null)
            lines.Add($"[yellow]{Markup.Escape(_loadError)}[/]");
        else if (_sessions.Count == 0)
            lines.Add("[grey70]В этом проекте пока нет сохранённых сессий.[/]");
        if (_sessions.Count > 0)
        {
            lines.Add(_loading ? "[grey70]↑↓ выбор · Esc отмена · открытие после загрузки[/]" :
                "[grey70]↑↓ выбор · Enter открыть · Esc отмена[/]");
            if (_sessions.Count > PageSize)
                lines.Add($"[grey70]←→ страницы · {_page + 1}/{(_sessions.Count + PageSize - 1) / PageSize} · {_sessions.Count} сессий[/]");
        }
        if (!_loading && _errors.Count > 0)
        {
            lines.Add("[yellow]Некоторые сессии не удалось прочитать:[/]");
            lines.Add(Markup.Escape(string.Join("\n", _errors.Take(3))));
        }
        lines.Add("[grey70]Прерванные действия автоматически не повторяются.[/]");
        _hint.SetContent(lines);
    }
}
