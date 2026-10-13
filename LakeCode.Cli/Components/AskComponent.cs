using LakeCode.Cli.Agent.Tools.Ask;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class AskComponent : PanelControl
{
    private readonly AskQuestion[] _questions;
    private readonly List<string>[] _answers;
    private readonly string[] _custom;
    private readonly Action<string[][]?> _complete;
    private readonly MarkupControl _title = new([]) { Wrap = true };
    private readonly ListControl _choices = new()
    {
        IsSelectable = true, MaxVisibleItems = 6,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HighlightBackgroundColor = new Color(35, 65, 75), HighlightForegroundColor = Color.White
    };
    private readonly PromptControl _editor = new()
    {
        Prompt = "[cyan]>[/] ", Placeholder = "Ваш ответ…", UnfocusOnEnter = false,
        Multiline = true, EnterBehavior = EnterBehavior.Submit, MinRows = 1, MaxRows = 3,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        InputBackgroundColor = Color.Transparent, InputFocusedBackgroundColor = Color.Transparent
    };
    private readonly MarkupControl _hint = new([]) { Wrap = true };
    private Window? _window;
    private int _index;
    private bool _editing;
    private bool _finished;
    private bool Reviewing => _index == _questions.Length;

    public AskComponent(AskQuestion[] questions, Action<string[][]?> complete)
    {
        _questions = questions;
        _answers = questions.Select(_ => new List<string>()).ToArray();
        _custom = new string[questions.Length];
        _complete = complete;
        BorderStyle = BorderStyle.Rounded;
        BorderColor = new Color(80, 130, 145);
        Padding = new Padding(1, 0, 1, 0);
        Margin = new Margin(1, 1, 1, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AddControl(_title);
        AddControl(_choices);
        AddControl(_editor);
        AddControl(_hint);
        _choices.ItemActivated += (_, _) => Activate();
        _editor.Entered += (_, _) => SaveCustom();
        Render();
    }

    public void Focus(Window window)
    {
        _window = window;
        window.PreviewKeyPressed += OnKey;
        FocusCurrent();
    }

    private void FocusCurrent()
    {
        _window?.FocusControl(_editing ? _editor : _choices);
    }

    private void Render()
    {
        _choices.ClearItems();
        _editor.Visible = false;
        _editing = false;
        if (Reviewing)
        {
            _title.Text = "[cyan bold]Подтвердите ответы[/]";
            for (var i = 0; i < _questions.Length; i++)
                _choices.AddItem(new ListItem($"{MarkupParser.Escape(_questions[i].Header)}: {MarkupParser.Escape(string.Join(", ", _answers[i]))}"));
            _choices.AddItem(new ListItem("[cyan]Отправить ответы[/]"));
            _choices.SelectedIndex = _questions.Length;
            _hint.Text = "[grey70]Enter — изменить / отправить · ← — назад · Esc — отменить запрос[/]";
        }
        else
        {
            var question = _questions[_index];
            _title.Text = $"[cyan bold]{MarkupParser.Escape(question.Header)}[/]  [grey70]{_index + 1}/{_questions.Length}[/]\n{MarkupParser.Escape(question.Question)}";
            foreach (var option in question.Options)
            {
                var mark = question.Multiple ? (_answers[_index].Contains(option.Label) ? "[x] " : "[ ] ") : "";
                _choices.AddItem(new ListItem($"{MarkupParser.Escape(mark + option.Label)}  [grey70]{MarkupParser.Escape(option.Description)}[/]"));
            }
            _choices.AddItem(new ListItem(string.IsNullOrEmpty(_custom[_index])
                ? "[cyan]Свой ответ…[/]" : $"[cyan]Свой ответ: {MarkupParser.Escape(_custom[_index])}[/]"));
            if (question.Multiple) _choices.AddItem(new ListItem("[cyan]Далее / подтвердить[/]"));
            _choices.SelectedIndex = 0;
            _hint.Text = question.Multiple
                ? "[grey70]↑↓ выбрать · Enter / Space отметить · → далее · ← назад · Esc отменить[/]"
                : "[grey70]↑↓ выбрать · Enter ответить · ← назад · Esc отменить[/]";
        }
        FocusCurrent();
    }

    private void Activate()
    {
        if (_finished) return;
        var selected = _choices.SelectedIndex;
        if (selected < 0) return;
        if (Reviewing)
        {
            if (selected == _questions.Length) Finish(_answers.Select(answer => answer.ToArray()).ToArray());
            else { _index = selected; Render(); }
            return;
        }
        var question = _questions[_index];
        if (selected == question.Options.Length)
        {
            _editing = true;
            _editor.Input = _custom[_index] ?? "";
            _editor.Visible = true;
            _hint.Text = "[grey70]Enter сохранить · Esc вернуться к вариантам[/]";
            FocusCurrent();
        }
        else if (selected > question.Options.Length) Next();
        else if (question.Multiple)
        {
            var label = question.Options[selected].Label;
            if (!_answers[_index].Remove(label)) _answers[_index].Add(label);
            Render();
            _choices.SelectedIndex = selected;
        }
        else
        {
            _answers[_index].Clear();
            _answers[_index].Add(question.Options[selected].Label);
            Next();
        }
    }

    private void SaveCustom()
    {
        var text = _editor.Input.Trim();
        if (text.Length is < 1 or > 4000)
        {
            _hint.Text = "[yellow]Введите ответ длиной от 1 до 4000 символов.[/]";
            return;
        }
        var question = _questions[_index];
        if (!question.Multiple) _answers[_index].Clear();
        else if (_custom[_index] is { } previous) _answers[_index].Remove(previous);
        _custom[_index] = text;
        if (!_answers[_index].Contains(text)) _answers[_index].Add(text);
        if (question.Multiple) Render();
        else Next();
    }

    private void Next()
    {
        if (_answers[_index].Count == 0)
        {
            _hint.Text = "[yellow]Выберите хотя бы один вариант или введите свой ответ.[/]";
            return;
        }
        if (_questions.Length == 1 && !_questions[0].Multiple)
        {
            Finish(_answers.Select(answer => answer.ToArray()).ToArray());
            return;
        }
        _index++;
        Render();
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || _finished || e.KeyInfo.Modifiers != 0) return;
        if (e.KeyInfo.Key == ConsoleKey.Escape)
        {
            e.Handled = true;
            if (_editing) Render();
            else Finish(null);
        }
        else if (!_editing && e.KeyInfo.Key == ConsoleKey.LeftArrow && _index > 0)
        {
            e.Handled = true;
            _index--;
            Render();
        }
        else if (!_editing && !Reviewing && e.KeyInfo.Key == ConsoleKey.RightArrow)
        {
            e.Handled = true;
            Next();
        }
        else if (!_editing && !Reviewing && _questions[_index].Multiple && e.KeyInfo.Key == ConsoleKey.Spacebar)
        {
            e.Handled = true;
            Activate();
        }
    }

    private void Finish(string[][]? answers)
    {
        if (_finished) return;
        Cancel();
        _complete(answers);
    }

    public void Cancel()
    {
        _finished = true;
        if (_window is not null) _window.PreviewKeyPressed -= OnKey;
        _window = null;
    }

    protected override void OnDisposing()
    {
        Cancel();
        base.OnDisposing();
    }
}
