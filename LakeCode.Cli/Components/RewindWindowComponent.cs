using LakeCode.Core.Conversations;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class RewindWindowComponent(ConsoleWindowSystem windowSystem, ChatController chat) : IDisposable
{
    private Window? _window;
    private Window? _parent;
    private InlinePromptControl? _editor;
    private MarkupControl? _status;
    private ListControl? _list;
    private bool _busy;

    public void Show(Window parent, InlinePromptControl editor)
    {
        if (_window is not null || !chat.CanRewind) return;
        var snapshot = chat.GetRewindPoints();
        if (snapshot.Points.IsEmpty) return;
        _parent = parent;
        _editor = editor;
        var surface = new Color(30, 36, 39);
        var height = Math.Min(30, parent.Height);
        var list = new ListControl
        {
            IsSelectable = true,
            BackgroundColor = surface,
            FocusedBackgroundColor = surface,
            HighlightBackgroundColor = new Color(35, 65, 75),
            HighlightForegroundColor = Color.White,
            ForegroundColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            Margin = new Margin(2, 1, 2, 0)
        };
        _list = list;
        foreach (var point in snapshot.Points)
        {
            var text = string.Join(" ", point.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > 65) text = text[..65] + "…";
            var role = point.Kind == RewindTargetKind.User ? "Вы" : "Агент";
            var unavailable = point.UnavailableReason is null ? "" : " [grey70]· недоступно[/]";
            list.AddItem(new ListItem($"{role,-5} {MarkupParser.Escape(text)}{unavailable}") { Tag = point });
        }
        list.SelectedIndex = snapshot.Points.Length - 1;
        var preview = new MarkupControl(new List<string>()) { Wrap = true, EnableSelection = true };
        var previewHost = new ScrollablePanelControl
        {
            Height = Math.Clamp(height / 4, 1, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Margin(2, 1, 2, 0),
            ShowScrollbar = true
        };
        previewHost.AddControl(preview);
        _status = new MarkupControl(new List<string>())
        {
            Wrap = true, Margin = new Margin(2, 1, 2, 0)
        };
        var hint = new MarkupControl(new List<string>
        {
            "[grey70]↑↓ выбрать · Enter откатить · Esc отменить[/]",
            "[grey70]Файлы и выполненные команды не откатываются.[/]"
        }) { Wrap = true, Margin = new Margin(2, 1, 2, 0) };

        void UpdateSelection()
        {
            if (list.SelectedItem?.Tag is not RewindPoint point) return;
            preview.Text = MarkupParser.Escape(point.Text);
            previewHost.ScrollToTop();
            _status!.Text = point.UnavailableReason is { } reason
                ? $"[yellow]{MarkupParser.Escape(reason)}[/]"
                : point.Kind == RewindTargetKind.User
                    ? "[cyan]Запрос и вложения вернутся в редактор. Это сообщение и продолжение будут удалены.[/]"
                    : "[cyan]Этот ответ останется в истории. Продолжение будет удалено; редактор будет пустым.[/]";
        }
        list.SelectedIndexChanged += (_, _) => UpdateSelection();
        UpdateSelection();
        var window = new WindowBuilder(windowSystem)
            .WithTitle("Откат истории")
            .WithSize(Math.Min(88, parent.Width), height)
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(surface).WithForegroundColor(Color.White)
            .AddControl(list).AddControl(previewHost).AddControl(_status).AddControl(hint)
            .Build();
        _window = window;
        window.PreviewKeyPressed += (_, e) =>
        {
            if (_busy) { e.Handled = true; return; }
            if (e.KeyInfo.Key != ConsoleKey.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        };
        list.ItemActivated += (_, _) =>
        {
            if (_busy || list.SelectedItem?.Tag is not RewindPoint point) return;
            if (point.UnavailableReason is { } reason)
            {
                _status!.Text = $"[yellow]{MarkupParser.Escape(reason)}[/]";
                return;
            }
            _busy = true;
            list.IsEnabled = false;
            _status!.Text = "[cyan]Откатываем историю…[/]";
            if (!chat.RewindTo(point, snapshot.SessionId, snapshot.TurnCount))
                OnCompleted("Откат недоступен: состояние диалога изменилось. Закройте окно и выберите точку повторно.");
        };
        window.OnClosing += (_, _) => Release();
        chat.RewindCompleted += OnCompleted;
        windowSystem.AddWindow(window);
        window.FocusControl(list);
    }

    private void OnCompleted(string? error)
    {
        if (_window is null || !_busy) return;
        _busy = false;
        if (error is null) { Close(); return; }
        _list!.IsEnabled = true;
        _status!.Text = $"[red]{MarkupParser.Escape(error)}[/]";
        _window.FocusControl(_list);
    }

    private void Close()
    {
        if (_window is { } window) windowSystem.CloseWindow(window);
        Release();
    }

    private void Release()
    {
        if (_window is null) return;
        chat.RewindCompleted -= OnCompleted;
        _window = null;
        _list = null;
        _status = null;
        _busy = false;
        if (_parent is { } parent && _editor is { } editor) parent.FocusControl(editor);
        _parent = null;
        _editor = null;
    }

    public void Dispose() => Close();
}
