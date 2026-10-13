using LakeCode.Core.Agent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class ExternalReadPermissionComponent : CollapsiblePanel
{
    private readonly MarkupControl _message;
    private readonly ListControl _choices;
    private readonly MarkupControl _hint;
    private readonly Action<ExternalReadDecision> _complete;
    private Window? _window;
    private bool _finished;

    public ExternalReadPermissionComponent(ExternalReadRequest request, Action<ExternalReadDecision> complete)
    {
        _complete = complete;
        Collapsible = false;
        ShowHeader = false;
        IsExpanded = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(2, 1, 2, 1);
        _message = new MarkupControl(new List<string>
        {
            "[yellow]Нужно разрешение на чтение вне рабочего каталога[/]",
            $"Папка: [bold]{MarkupParser.Escape(request.Directory)}[/]",
            $"Путь: {MarkupParser.Escape(request.Path)}",
            "Только чтение этой папки и подпапок. Выбор действует до /new или выхода."
        })
        {
            Wrap = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _choices = new ListControl
        {
            IsSelectable = true,
            MaxVisibleItems = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HighlightBackgroundColor = new Color(35, 65, 75),
            HighlightForegroundColor = Color.White,
            Margin = new Margin(0, 1, 0, 0)
        };
        _choices.AddItem(new ListItem("Запретить до конца сессии") { Tag = ExternalReadDecision.DenySession });
        _choices.AddItem(new ListItem("Разрешить до конца сессии") { Tag = ExternalReadDecision.AllowSession });
        _choices.SelectedIndex = 0;
        _choices.ItemActivated += (_, _) =>
        {
            if (_choices.SelectedItem?.Tag is ExternalReadDecision decision) Finish(decision);
        };
        AddControl(_message);
        AddControl(_choices);
        _hint = new MarkupControl(new List<string> { "[grey70]↑↓ выбрать · Enter подтвердить · Esc отклонить этот вызов[/]" });
        AddControl(_hint);
    }

    public void Focus(Window window)
    {
        _window = window;
        window.PreviewKeyPressed += OnPreviewKeyPressed;
        window.FocusControl(_choices);
    }

    private void OnPreviewKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || e.KeyInfo.Key != ConsoleKey.Escape) return;
        e.Handled = true;
        Finish(ExternalReadDecision.DenyOnce);
    }

    private void Finish(ExternalReadDecision decision)
    {
        if (_finished) return;
        _finished = true;
        DetachKeys();
        _choices.Visible = false;
        _hint.Visible = false;
        _message.Text = decision switch
        {
            ExternalReadDecision.AllowSession => "[green]Чтение разрешено до конца сессии.[/]",
            ExternalReadDecision.DenySession => "[yellow]Чтение запрещено до конца сессии.[/]",
            _ => "[yellow]Текущий вызов отклонён.[/]"
        };
        _complete(decision);
    }

    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        DetachKeys();
        _choices.Visible = false;
        _hint.Visible = false;
        _message.Text = "[grey70]Подтверждение отменено.[/]";
    }

    private void DetachKeys()
    {
        if (_window is null) return;
        _window.PreviewKeyPressed -= OnPreviewKeyPressed;
        _window.FocusControl(this);
        _window = null;
    }
}
