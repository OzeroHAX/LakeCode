using System.Drawing;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Color = SharpConsoleUI.Color;

namespace LakeCode.Cli.Components;

internal sealed class SlashCommandsComponent : PortalContentContainer, IDisposable
{
    private readonly IReadOnlyList<(string Name, string Description)> _commands;

    private readonly Window _window;
    private readonly InputComponent _input;
    private static readonly Color Surface = new(30, 36, 39);
    private readonly ListControl _list = new()
    {
        BackgroundColor = Surface,
        FocusedBackgroundColor = Surface,
        ForegroundColor = Color.White,
        HighlightBackgroundColor = new Color(35, 65, 75),
        HighlightForegroundColor = Color.White,
        Margin = new Margin(1, 0, 1, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsSelectable = true
    };
    private readonly MarkupControl _hint = new([])
    {
        Wrap = false,
        Height = 1,
        Margin = new Margin(1, 0, 1, 0)
    };
    private LayoutNode? _portal;

    private string? SelectedCommand => _portal != null && _list.SelectedIndex >= 0
        ? (string?)_list.Items[_list.SelectedIndex].Tag : null;

    /// <summary>
    /// Raised on Enter after completing a known command. The component does not execute it.
    /// </summary>
    public event EventHandler<(string Name, string Arguments)>? CommandSelected;

    public SlashCommandsComponent(Window window, InputComponent input,
        IReadOnlyList<(string Name, string Description)> commands)
    {
        _window = window;
        _input = input;
        _commands = commands;
        BackgroundColor = Surface;
        AddChild(_list);
        AddChild(_hint);
        PortalFocusedControl = _list;
        DismissOnOutsideClick = true;
        DismissRequested += (_, _) => Close();
        _window.PreviewKeyPressed += OnKeyPressed;
        _input.Editor.InputChanged += OnInputChanged;
        _input.Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_input.Editor.IsEnabled || !_input.Editor.HasFocus)
            Close();
    }

    private void OnInputChanged(object? sender, string text)
    {
        Close();
        _list.ClearItems();
        if (!text.StartsWith('/') || text.Any(char.IsWhiteSpace))
            return;

        foreach (var command in _commands)
        {
            if (command.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                _list.AddItem(new ListItem($"{command.Name}  [grey70]{command.Description}[/]")
                {
                    Tag = command.Name
                });
        }
        var count = _list.Items.Count;
        _list.SelectedIndex = count > 0 ? 0 : -1;
        _list.Visible = count > 0;
        _list.Height = _list.MaxVisibleItems = count;
        _hint.SetContent([count > 0
            ? "[grey70]↑↓ выбор · Tab дополнить · Enter выбрать · Esc закрыть[/]"
            : "[grey70]Нет подходящих команд · Esc закрыть[/]"]);
        _portal = _window.CreatePortal(_input.Editor, this);
    }

    private void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (!_input.Editor.HasFocus || !_input.Editor.IsEnabled || e.Handled || e.KeyInfo.Modifiers != 0)
            return;

        var selected = SelectedCommand;
        switch (e.KeyInfo.Key)
        {
            case ConsoleKey.Escape when _portal != null:
                Close();
                break;
            case ConsoleKey.UpArrow or ConsoleKey.DownArrow when selected != null:
                _list.ProcessKey(e.KeyInfo);
                break;
            case ConsoleKey.Tab when selected != null:
                _input.Editor.SetInput(selected.Equals("/btw", StringComparison.OrdinalIgnoreCase) ? selected + " " : selected);
                break;
            case ConsoleKey.Enter when _input.Editor.Input.StartsWith('/'):
                e.Handled = true; // Never submit slash commands to the chat.
                SelectCommand(selected ?? _input.Editor.Input);
                return;
            default:
                return;
        }
        e.Handled = true;
    }

    private void SelectCommand(string command)
    {
        var separator = command.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = separator < 0 ? command : command[..separator];
        var arguments = separator < 0 ? "" : command[(separator + 1)..].TrimStart();
        if (!_commands.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Close();
            _input.ShowError("Неизвестная команда. Черновик сохранён.");
            return;
        }
        if (name.Equals("/btw", StringComparison.OrdinalIgnoreCase) &&
            !_input.Editor.Input.Equals(name, StringComparison.OrdinalIgnoreCase) && separator < 0)
        {
            _input.Editor.SetInput(name + " ");
            Close();
            return;
        }
        if (!name.Equals("/btw", StringComparison.OrdinalIgnoreCase) && arguments.Length > 0)
        {
            Close();
            _input.ShowError($"Команда {name} не принимает аргументы. Черновик сохранён.");
            return;
        }
        if (separator < 0) _input.Editor.SetInput(name);
        Close();
        CommandSelected?.Invoke(this, (name, arguments));
    }

    private void Close()
    {
        if (_portal == null)
            return;
        var portal = _portal;
        _portal = null;
        _window.RemovePortal(_input.Editor, portal);
    }

    public override Rectangle GetPortalBounds()
    {
        var cursor = _window.GetCursorContentPosition(_input.Editor);
        var anchor = new Rectangle(_input.ActualX, cursor?.Y ?? _input.ActualY, _input.ActualWidth, 1);
        var width = Math.Min(64, Math.Max(1, anchor.Width));
        return PortalPositioner.Calculate(new PortalPositionRequest(
            anchor, new Size(width, _list.Items.Count + 1),
            new Rectangle(0, 0, _window.Width, _window.Height),
            PortalPlacement.AboveOrBelow, 0)).Bounds;
    }

    public new void Dispose()
    {
        Close();
        _window.PreviewKeyPressed -= OnKeyPressed;
        _input.Editor.InputChanged -= OnInputChanged;
        _input.Editor.PropertyChanged -= OnEditorPropertyChanged;
        base.Dispose();
    }
}
