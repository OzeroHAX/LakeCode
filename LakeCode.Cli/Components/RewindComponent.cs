using SharpConsoleUI;

namespace LakeCode.Cli.Components;

/// <summary>Opens the modal history selector on double Escape from an empty editor.</summary>
internal sealed class RewindComponent : IDisposable
{
    private readonly Window _window;
    private readonly InputComponent _input;
    private readonly ChatController _chat;
    private readonly RewindWindowComponent _selector;
    private long _lastEscape;

    public RewindComponent(Window window, InputComponent input, ChatController chat, RewindWindowComponent selector)
    {
        _window = window;
        _input = input;
        _chat = chat;
        _selector = selector;
        window.PreviewKeyPressed += OnKey;
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || e.KeyInfo.Modifiers != 0) return;
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
            _selector.Show(_window, _input.Editor);
        }
        else _lastEscape = now;
    }

    public void Dispose() => _window.PreviewKeyPressed -= OnKey;
}
