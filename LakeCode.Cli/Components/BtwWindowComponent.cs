using LakeCode.Core.Conversations;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

/// <summary>Ephemeral side answers in a separate modal keyboard scope, never in the main transcript.</summary>
internal sealed class BtwWindowComponent(ConsoleWindowSystem windowSystem) : IDisposable
{
    private SideQuestionState _state = new(0, false, []);
    private Window? _window;
    private Guid? _spinnerRequestId;
    private Guid? _selectedId;
    private MarkupControl? _question, _answer, _status, _usage, _hint;
    private ScrollablePanelControl? _viewport;
    private SpinnerTextAnimator? _spinner;

    public bool IsOpen => _window is not null;
    public event Action<Guid>? CancelRequested;
    public event Action? ClearRequested;
    public event Action? Closed;

    public void Apply(SideQuestionState state)
    {
        if (state.Revision <= _state.Revision) return;
        var previousLatest = _state.Exchanges.LastOrDefault()?.RequestId;
        _state = state;
        if (state.Exchanges.IsEmpty)
        {
            Close(cancel: false);
            _selectedId = null;
            return;
        }
        if (previousLatest != state.Exchanges[^1].RequestId || !state.Exchanges.Any(item => item.RequestId == _selectedId))
            _selectedId = state.Exchanges[^1].RequestId;
        Render();
    }

    public bool Show(Window parent)
    {
        if (_state.Exchanges.IsEmpty) return false;
        if (_window is not null) return true;
        _selectedId = _state.Exchanges[^1].RequestId;
        var surface = new Color(30, 36, 39);
        _question = new MarkupControl([]) { Wrap = true, Margin = new Margin(0, 0, 0, 1) };
        _answer = new MarkupControl([]) { Wrap = true, EnableSelection = true };
        _status = new MarkupControl([]) { Wrap = true, Margin = new Margin(2, 1, 2, 0) };
        _usage = new MarkupControl([]) { Wrap = true, Margin = new Margin(2, 1, 2, 0) };
        _hint = new MarkupControl([]) { Wrap = true, Margin = new Margin(2, 1, 2, 0) };
        _viewport = new ScrollablePanelControl
        {
            BorderStyle = BorderStyle.None,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
            Padding = new Padding(2, 1, 2, 1),
            ShowScrollbar = true
        };
        _viewport.AddControl(_question);
        _viewport.AddControl(_answer);
        _window = new WindowBuilder(windowSystem)
            .WithTitle("Попутный вопрос · /btw")
            .WithSize(Math.Min(84, parent.Width), Math.Min(26, parent.Height))
            .Centered().AsModal().HideTitleButtons()
            .Minimizable(false).Maximizable(false).Resizable(false).Movable(false)
            .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
            .WithBackgroundColor(surface).WithForegroundColor(Color.White)
            .AddControl(new MarkupControl(["[grey70]Без инструментов · не входит в основную историю[/]"])
                { Wrap = true, Margin = new Margin(2, 1, 2, 0) })
            .AddControl(_status).AddControl(_viewport).AddControl(_usage).AddControl(_hint)
            .Build();
        _window.PreviewKeyPressed += OnKey;
        _window.OnClosing += (_, _) =>
        {
            if (_window is null) return;
            CancelActive();
            ReleaseWindow();
            Closed?.Invoke();
        };
        Render();
        windowSystem.AddWindow(_window);
        _window.FocusControl(_viewport);
        return true;
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || e.KeyInfo.Modifiers != 0) return;
        switch (e.KeyInfo.Key)
        {
            case ConsoleKey.Escape:
                e.Handled = true;
                Close(cancel: true);
                break;
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
                e.Handled = true;
                var index = _state.Exchanges.IndexOf(_state.Exchanges.First(item => item.RequestId == _selectedId));
                var next = Math.Clamp(index + (e.KeyInfo.Key == ConsoleKey.LeftArrow ? -1 : 1), 0, _state.Exchanges.Length - 1);
                _selectedId = _state.Exchanges[next].RequestId;
                Render();
                _viewport?.ScrollToTop();
                break;
            case ConsoleKey.X:
                e.Handled = true;
                Close(cancel: true);
                ClearRequested?.Invoke();
                break;
            case ConsoleKey.Enter when !_state.IsGenerating:
                e.Handled = true;
                Close(cancel: false);
                break;
        }
    }

    private void Render()
    {
        if (_window is null || _answer is null || _state.Exchanges.IsEmpty) return;
        var exchange = _state.Exchanges.FirstOrDefault(item => item.RequestId == _selectedId) ?? _state.Exchanges[^1];
        _question!.Text = $"[bold]{MarkupParser.Escape(exchange.Question)}[/]";
        _answer.SetMarkdown(exchange.Answer);
        var active = exchange.Status == "in_progress";
        if (!active || _spinnerRequestId != exchange.RequestId)
        {
            _spinner?.Dispose();
            _spinner = null;
            _spinnerRequestId = null;
        }
        var model = MarkupParser.Escape($"{exchange.ProviderName} · {exchange.Model.Name} · {exchange.ReasoningEffort}");
        _status!.Text = active ? $"[cyan]Попутный ответ…[/]  [grey70]{model}[/]" :
            exchange.Error is { } error ? $"[red]{MarkupParser.Escape(error)}[/]\n[grey70]{model}[/]" : $"[grey70]{model}[/]";
        if (active && _spinner is null)
        {
            _spinnerRequestId = exchange.RequestId;
            _spinner = new SpinnerTextAnimator(windowSystem, SpinnerStyle.Dots,
                frame => { if (_status is not null) _status.Text = $"[cyan]Попутный ответ{frame}[/]  [grey70]{model}[/]"; });
            _spinner.Start();
        }
        var usage = exchange.Statistics;
        static string Value(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        _usage!.Text = $"[grey70]input: {Value(usage?.InputTokens)} · cached: {Value(usage?.CachedInputTokens)} · output: {Value(usage?.OutputTokens)}\n" +
            $"reasoning: {Value(usage?.ReasoningTokens)} · total: {Value(usage?.TotalTokens)}[/]\n" +
            (usage?.CachedInputTokens is null ? "[grey70]Использование кэша неизвестно.[/]" :
                usage.CachedInputTokens == 0 ? "[yellow]Провайдер сообщил cached: 0 — cache hit не подтверждён.[/]" :
                "[grey70]Cached — фактический usage провайдера, не гарантия стоимости.[/]");
        _hint!.Text = $"[grey70]←→ ответы · Esc закрыть{(_state.IsGenerating ? " и отменить только /btw" : "")} · x очистить\n" +
            $"{_state.Exchanges.IndexOf(exchange) + 1}/{_state.Exchanges.Length} · Новый вопрос: /btw вопрос[/]";
        if (active) _viewport!.ScrollToBottom();
    }

    private void CancelActive()
    {
        if (_state.Exchanges.LastOrDefault(item => item.Status == "in_progress") is { } active)
            CancelRequested?.Invoke(active.RequestId);
    }

    private void Close(bool cancel)
    {
        if (_window is not { } window) return;
        if (cancel) CancelActive();
        ReleaseWindow();
        windowSystem.CloseWindow(window);
        Closed?.Invoke();
    }

    private void ReleaseWindow()
    {
        _spinner?.Dispose();
        _spinner = null;
        if (_window is not null) _window.PreviewKeyPressed -= OnKey;
        _window = null;
        _spinnerRequestId = null;
        _question = _answer = _status = _usage = _hint = null;
        _viewport = null;
    }

    public void Dispose() => Close(cancel: true);
}
