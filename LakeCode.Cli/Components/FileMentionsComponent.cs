using System.Drawing;
using LakeCode.Core.Attachments;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using Color = SharpConsoleUI.Color;

namespace LakeCode.Cli.Components;

/// <summary>Workspace-only file picker. A selection creates a draft attachment, not a parsed text mention.</summary>
internal sealed class FileMentionsComponent : PortalContentContainer, IDisposable
{
    private readonly Window _window;
    private readonly ConsoleWindowSystem _system;
    private readonly InputComponent _input;
    private readonly string _root;
    private readonly CancellationToken _shutdown;
    private readonly ListControl _list = new()
    {
        BackgroundColor = new Color(30, 36, 39), FocusedBackgroundColor = new Color(30, 36, 39),
        ForegroundColor = Color.White, HighlightBackgroundColor = new Color(35, 65, 75),
        HighlightForegroundColor = Color.White, Margin = new Margin(1, 0, 1, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch, IsSelectable = true, MaxVisibleItems = 10
    };
    private readonly MarkupControl _hint = new([]) { Height = 1, Wrap = false, Margin = new Margin(1, 0, 1, 0) };
    private LayoutNode? _portal;
    private CancellationTokenSource? _search;
    private int _start, _end;
    private bool _disposed, _selecting;

    public FileMentionsComponent(Window window, ConsoleWindowSystem system, InputComponent input,
        string root, CancellationToken shutdown)
    {
        _window = window;
        _system = system;
        _input = input;
        _root = root;
        _shutdown = shutdown;
        BackgroundColor = new Color(30, 36, 39);
        AddChild(_list);
        AddChild(_hint);
        PortalFocusedControl = _list;
        DismissOnOutsideClick = true;
        DismissRequested += (_, _) => Close();
        _list.ItemActivated += (_, _) => Select();
        input.Editor.InputChanged += OnInputChanged;
        input.Editor.PropertyChanged += OnPropertyChanged;
        window.PreviewKeyPressed += OnKeyPressed;
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_input.Editor.HasFocus || !_input.Editor.IsEnabled) Close();
    }

    private void OnInputChanged(object? sender, string text)
    {

        Close();
        if (_selecting || _disposed || !_input.Editor.HasFocus || text.StartsWith('/')) return;
        var end = _input.Editor.Caret;
        var start = end > 0 ? text.LastIndexOf('@', end - 1, end) : -1;
        if (start < 0 || (start > 0 && !char.IsWhiteSpace(text[start - 1]))) return;
        var query = text[(start + 1)..end];
        if (query.Any(char.IsWhiteSpace) || query.Length > 256) return;
        _start = start;
        _end = end;
        _list.ClearItems();
        _list.SelectedIndex = -1;
        _list.Visible = false;
        _hint.SetContent(["[cyan]Поиск файлов проекта…[/]"]);
        _portal = _window.CreatePortal(_input.Editor, this);
        var search = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        _search = search;
        _ = SearchAsync(query, text, search);
    }

    private async Task SearchAsync(string query, string text, CancellationTokenSource search)
    {
        FileSearchResult? result = null;
        string? error = null;
        try
        {
            await Task.Delay(120, search.Token);
            result = await Task.Run(() => FileAttachments.Search(_root, query, search.Token), search.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { error = failure.Message; }
        _system.EnqueueOnUIThread(() =>
        {
            try
            {
                if (_disposed || _search != search || search.IsCancellationRequested || _input.Editor.Input != text) return;
                _search = null;
                _list.ClearItems();
                foreach (var path in result?.Paths ?? [])
                    _list.AddItem(new ListItem(MarkupParser.Escape(path)) { Tag = path });
                _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
                _list.Visible = _list.Items.Count > 0;
                _list.MaxVisibleItems = Math.Max(1, _list.Items.Count);
                _hint.SetContent([error is not null ? $"[red]{MarkupParser.Escape(error)}[/]"
                    : _list.Items.Count == 0 ? "[grey70]Нет текстовых файлов · Esc закрыть[/]"
                    : "[grey70]↑↓ выбор · Tab / Enter прикрепить · Esc закрыть[/]" +
                      (result!.Truncated ? " [yellow]· список ограничен: уточните путь[/]" : "") +
                      (result.Skipped > 0 ? $" [yellow]· пропущено: {result.Skipped}[/]" : "")]);
            }
            finally { search.Dispose(); }
        });
    }

    private void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (_portal is null || e.Handled || !_input.Editor.HasFocus || e.KeyInfo.Modifiers != 0) return;
        switch (e.KeyInfo.Key)
        {
            case ConsoleKey.Escape:
                Close();
                break;
            case ConsoleKey.UpArrow or ConsoleKey.DownArrow:
                if (_list.Items.Count > 0) _list.ProcessKey(e.KeyInfo);
                break;
            case ConsoleKey.Enter or ConsoleKey.Tab:
                Select();
                break;
            case ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End:
                Close();
                return;
            default: return;
        }
        e.Handled = true;
    }

    private void Select()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _list.Items.Count) return;
        var path = (string)_list.Items[_list.SelectedIndex].Tag!;
        _selecting = true;
        try
        {
            if (!_input.AddFile(path, _start, _end - _start)) return;
            Close();
            _window.FocusControl(_input.Editor);
        }
        finally { _selecting = false; }
    }

    private void Close()
    {
        _search?.Cancel();
        _search = null;
        if (_portal is not { } portal) return;
        _portal = null;
        _window.RemovePortal(_input.Editor, portal);
    }

    public override Rectangle GetPortalBounds()
    {
        var cursor = _window.GetCursorContentPosition(_input.Editor);
        var anchor = new Rectangle(_input.ActualX, cursor?.Y ?? _input.ActualY, _input.ActualWidth, 1);
        return PortalPositioner.Calculate(new PortalPositionRequest(anchor,
            new Size(Math.Min(80, Math.Max(1, anchor.Width)), Math.Max(1, _list.Items.Count) + 1),
            new Rectangle(0, 0, _window.Width, _window.Height), PortalPlacement.AboveOrBelow, 0)).Bounds;
    }

    public new void Dispose()
    {
        _disposed = true;
        Close();
        _input.Editor.InputChanged -= OnInputChanged;
        _input.Editor.PropertyChanged -= OnPropertyChanged;
        _window.PreviewKeyPressed -= OnKeyPressed;
    }
}
