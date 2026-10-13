using LakeCode.Cli;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

namespace LakeCode.Cli.Components;

/// <summary>Keyboard selection of user turns in the existing transcript.</summary>
internal sealed class RewindComponent : IDisposable
{
    private readonly Window _window;
    private readonly MainContentComponent _content;
    private readonly InputComponent _input;
    private readonly ChatController _chat;
    private readonly MarkupControl _hint;
    private int? _selected;
    private long _lastEscape;
    private UserMessageComponent[] _messages = [];

    public RewindComponent(Window window, MainContentComponent content, InputComponent input, ChatController chat)
    {
        _window = window;
        _content = content;
        _input = input;
        _chat = chat;
        _hint = new MarkupControl(new List<string>()) { Visible = false, Wrap = true };
        input.AddControl(_hint);
        window.PreviewKeyPressed += OnKey;
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || e.KeyInfo.Modifiers != 0) return;
        if (_selected is { } selected)
        {
            e.Handled = true;
            if (!_chat.CanRewind) { Close(); return; }
            if (e.KeyInfo.Key == ConsoleKey.Escape) Close();
            else if (e.KeyInfo.Key == ConsoleKey.UpArrow) Select(Math.Max(0, selected - 1));
            else if (e.KeyInfo.Key == ConsoleKey.DownArrow) Select(Math.Min(_messages.Length - 1, selected + 1));
            else if (e.KeyInfo.Key == ConsoleKey.Enter)
            {
                Close();
                _chat.RewindTo(selected);
            }
            return;
        }
        if (e.KeyInfo.Key != ConsoleKey.Escape || !_input.Editor.HasFocus || !_chat.CanRewind)
        {
            _lastEscape = 0;
            return;
        }
        e.Handled = true;
        var now = Environment.TickCount64;
        if (_lastEscape != 0 && now - _lastEscape < 500)
        {
            _lastEscape = 0;
            _messages = _content.Children.OfType<UserMessageComponent>().ToArray();
            if (_messages.Length == 0) return;
            _content.AutoScroll = false;
            _hint.Text = "[cyan]↑↓ выбрать · Enter вернуть в редактор и удалить продолжение · Esc отменить[/]\n[grey]Файлы и выполненные команды не откатываются.[/]";
            _hint.Visible = true;
            Select(_messages.Length - 1);
        }
        else _lastEscape = now;
    }

    private void Select(int index)
    {
        if (_selected is { } previous) _messages[previous].SetRewindSelected(false);
        _selected = index;
        _messages[index].SetRewindSelected(true);
        _content.ScrollChildIntoView(_messages[index]);
    }

    private void Close()
    {
        if (_selected is { } index) _messages[index].SetRewindSelected(false);
        _selected = null;
        _messages = [];
        _hint.Visible = false;
        _content.AutoScroll = true;
        _window.FocusControl(_input.Editor);
    }

    public void Dispose()
    {
        Close();
        _window.PreviewKeyPressed -= OnKey;
    }
}
