using System.Diagnostics;
using System.Text.Json;
using LakeCode.Core.Agent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class BashToolCallComponent : CollapsiblePanel
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly MarkupControl _activity = new(new List<string>());
    private readonly MarkupControl _output = new(new List<string>());
    private readonly ScrollablePanelControl _viewport = new();
    private readonly ButtonControl _follow = new();
    private readonly ButtonControl _pause = new();
    private readonly SpinnerTextAnimator _spinner;
    private readonly string _directory;
    private readonly long _created = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _clock = new();
    private readonly Task _clockTask;
    private BashExecution? _execution;
    private ToolCallStatus _status;
    private string? _result;
    private string _frame = "";
    private string _displayed = "";
    private double? _finishedSeconds;
    private bool _paused;
    private bool _disposed;

    public BashToolCallComponent(ConsoleWindowSystem windowSystem, ToolCallUpdate update)
    {
        _windowSystem = windowSystem;
        HeaderStyle = CollapsibleHeaderStyle.Rounded;
        IsExpanded = true;
        MaxContentHeight = int.MaxValue;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 0, 0, 1);
        Padding = new Padding(1, 1, 1, 1);
        BackgroundColor = new Color(26, 26, 26);
        BorderColor = new Color(85, 85, 85);
        ForegroundColor = new Color(185, 185, 185);
        FocusedBackgroundColor = new Color(45, 45, 45);

        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        if (root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
            AddControl(new MarkupControl(new List<string> { MarkupParser.Escape(description.GetString() ?? "") })
            { Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        var command = root.TryGetProperty("command", out var value) ? value.GetString() ?? "" : "";
        _directory = root.TryGetProperty("workingDirectory", out var directory) ? directory.GetString() ?? "." : ".";
        AddControl(new MarkupControl(new List<string> { $"[bold]$ {MarkupParser.Escape(command)}[/]" })
        {
            Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch,
            BackgroundColor = BackgroundColor, ForegroundColor = ForegroundColor
        });
        _activity.Wrap = true;
        _activity.HorizontalAlignment = HorizontalAlignment.Stretch;
        _activity.ForegroundColor = new Color(155, 155, 155);
        _activity.Margin = new Margin(0, 0, 0, 1);
        AddControl(_activity);

        // Same bounded auto-sizing viewport as UserMessageComponent; no fixed Height.
        _viewport.BorderStyle = BorderStyle.None;
        _viewport.HorizontalAlignment = HorizontalAlignment.Stretch;
        _viewport.ShowScrollbar = true;
        _viewport.ScrollbarColor = new Color(50, 50, 50);
        _viewport.ScrollbarThumbColor = new Color(105, 105, 105);
        _viewport.AutoScroll = true;
        _output.Wrap = true;
        _output.EnableSelection = true;
        _output.HorizontalAlignment = HorizontalAlignment.Stretch;
        _output.BackgroundColor = BackgroundColor;
        _output.ForegroundColor = ForegroundColor;
        _viewport.AddControl(_output);
        AddControl(_viewport);

        _follow.Text = "К свежему выводу";
        _follow.Visible = false;
        _follow.BackgroundColor = Color.Transparent;
        _follow.ForegroundColor = new Color(155, 195, 205);
        _follow.Click += (_, _) =>
        {
            _paused = false;
            ShowOutput();
            _viewport.AutoScroll = true;
            _viewport.ScrollToBottom();
            RenderStatus();
        };
        AddControl(_follow);
        _pause.Text = "Приостановить обновление лога";
        _pause.BackgroundColor = Color.Transparent;
        _pause.ForegroundColor = new Color(155, 155, 155);
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            if (!_paused) ShowOutput();
            RenderStatus();
        };
        AddControl(_pause);
        _viewport.Scrolled += (_, _) => RenderStatus();
        _spinner = new SpinnerTextAnimator(windowSystem, SpinnerStyle.Dots, frame =>
        {
            _frame = frame;
            RenderStatus();
        });
        Update(update);
        _clockTask = TickAsync(_clock.Token);
    }

    public void Update(ToolCallUpdate update)
    {
        _status = update.Status;
        _result = update.Result;
        _execution = update.BashExecution ?? _execution;
        if (_status == ToolCallStatus.Running) _spinner.Start();
        else if (_status == ToolCallStatus.AwaitingApproval) _spinner.Stop();
        else StopActivity();
        if (!_paused) ShowOutput();
        RenderStatus();
    }

    public void UpdateOutput(BashExecution execution)
    {
        if (_status != ToolCallStatus.Running || _disposed) return;
        _execution = execution;
        if (!_paused) ShowOutput();
        RenderStatus();
    }

    public void FinishPending()
    {
        if (_status is not (ToolCallStatus.Running or ToolCallStatus.AwaitingApproval)) return;
        _status = ToolCallStatus.Cancelled;
        _result = "Запрос завершён до получения результата команды.";
        StopActivity();
        if (!_paused) ShowOutput();
        RenderStatus();
    }

    private void ShowOutput()
    {
        var text = _execution is null ? BashOutputText.Normalize(_result ?? "Вывод пока не поступал.")
            : BashOutputText.Normalize(_execution.Output);
        if (_execution?.Truncated == true) text += "\n[Вывод ограничен 32 000 символами.]";
        if (_execution is not null && _status == ToolCallStatus.Cancelled)
            text += "\nКоманда отменена; полученный вывод сохранён.";
        else if (_execution is not null && _status == ToolCallStatus.Failed && _result is not null &&
                 !_result.StartsWith("Error: Exit code:", StringComparison.Ordinal))
            text += "\n" + BashOutputText.Normalize(_result);
        if (text.Length == 0) text = _status == ToolCallStatus.Running ? "Вывод пока не поступал." : "Команда не вывела текст.";
        if (text == _displayed) return;
        // Append only when it is a true extension. CR progress and completion may replace text.
        if (_displayed.Length > 0 && text.StartsWith(_displayed, StringComparison.Ordinal))
            _output.AppendInline(MarkupParser.Escape(text[_displayed.Length..]));
        else _output.Text = MarkupParser.Escape(text);
        _displayed = text;
    }

    private void RenderStatus()
    {
        if (_disposed) return;
        var (label, color) = _status switch
        {
            ToolCallStatus.Running => ("выполняется", "cyan"),
            ToolCallStatus.AwaitingApproval => ("ожидает подтверждения", "yellow"),
            ToolCallStatus.Completed => ("завершено", "green"),
            ToolCallStatus.Failed => ("ошибка", "red"),
            _ => ("отменено", "yellow")
        };
        var seconds = _execution?.ElapsedSeconds ?? _finishedSeconds ?? Stopwatch.GetElapsedTime(_created).TotalSeconds;
        if (_status == ToolCallStatus.Running && _execution is not null)
            seconds += Math.Max(0, (DateTimeOffset.UtcNow - _execution.StartedAt).TotalSeconds - _execution.ElapsedSeconds);
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var time = $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
        var exit = _status != ToolCallStatus.Running && _execution?.ExitCode is { } code ? $" · exit {code}" : "";
        Title = $"[bold]Bash[/] · [{color}]{label}{(_status == ToolCallStatus.Running ? " " + _frame : "")}[/] · {time}{exit}";
        var activity = _status == ToolCallStatus.AwaitingApproval ? "команда ещё не запущена" : _status == ToolCallStatus.Running
            ? _execution?.LastOutputAt is { } last
                ? $"последний вывод {Math.Max(0, (int)(DateTimeOffset.UtcNow - last).TotalSeconds)} с назад"
                : _execution is null ? "запуск команды…" : "процесс запущен · вывод пока не поступал"
            : "полученный вывод сохранён";
        var activityText = MarkupParser.Escape($"{_directory} · {activity}" + (_paused ? " · обновление лога приостановлено" : ""));
        if (_activity.Text != activityText) _activity.Text = activityText;
        _follow.Visible = _paused || !_viewport.AutoScroll;
        _pause.Visible = _status == ToolCallStatus.Running || _paused;
        _pause.Text = _paused ? "Возобновить обновление лога" : "Приостановить обновление лога";
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                _windowSystem.EnqueueOnUIThread(() =>
                {
                    if (!_disposed && _status == ToolCallStatus.Running) RenderStatus();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void StopActivity()
    {
        _finishedSeconds ??= Stopwatch.GetElapsedTime(_created).TotalSeconds;
        _spinner.Stop();
        _clock.Cancel();
    }

    protected override void OnDisposing()
    {
        _disposed = true;
        _spinner.Dispose();
        _clock.Cancel();
        // TickAsync catches cancellation; release the source after its pending wait has ended.
        _ = _clockTask.ContinueWith(_ => _clock.Dispose(), TaskScheduler.Default);
        base.OnDisposing();
    }
}
