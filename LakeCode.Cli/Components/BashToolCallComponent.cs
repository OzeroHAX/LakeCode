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
    private const string RunningPlaceholder = "ожидание вывода…";
    private const string EmptyPlaceholder = "команда не вывела текст";

    private readonly ConsoleWindowSystem _windowSystem;
    private readonly ExecutionAssessmentComponent _assessment = new();
    private ExecutionApprovalComponent? _approval;
    private readonly MarkupControl _output = new(new List<string>());
    private readonly MarkupControl _note = new(new List<string>()) { Visible = false };
    private readonly ScrollablePanelControl _viewport = new();
    private readonly ButtonControl _follow = new();
    private readonly ButtonControl _pause = new();
    private readonly SpinnerTextAnimator _spinner;
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
        BackgroundColor = ToolCardStyle.Surface;
        BorderColor = ToolCardStyle.Border;
        ForegroundColor = ToolCardStyle.Output;
        FocusedBackgroundColor = ToolCardStyle.Focused;

        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        var described = root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String;
        if (described)
            AddControl(new MarkupControl(new List<string> { MarkupParser.Escape(description.GetString() ?? "") })
            {
                Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch,
                ForegroundColor = ToolCardStyle.Description
            });
        var command = root.TryGetProperty("command", out var value) ? value.GetString() ?? "" : "";
        var directory = root.TryGetProperty("workingDirectory", out var directoryValue) ? directoryValue.GetString() ?? "." : ".";
        // The prompt carries the directory, so the card needs no separate activity line.
        AddControl(new MarkupControl(new List<string>
        {
            $"[{ToolCardStyle.Prompt.ToMarkup()}]{MarkupParser.Escape(directory)} $[/] " +
            $"[{ToolCardStyle.Command.ToMarkup()}]{MarkupParser.Escape(command)}[/]"
        })
        {
            Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch,
            BackgroundColor = ToolCardStyle.CommandSurface, Padding = new Padding(1, 0, 1, 0),
            ForegroundColor = ToolCardStyle.Command, Margin = new Margin(0, described ? 1 : 0, 0, 1)
        });

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
        _output.BackgroundColor = ToolCardStyle.Surface;
        _output.ForegroundColor = ToolCardStyle.Output;
        _output.Padding = new Padding(1, 0, 1, 0);
        _viewport.AddControl(_output);
        AddControl(_viewport);
        AddControl(_assessment);

        // Warnings and failures live below the output instead of being glued into it.
        _note.Wrap = true;
        _note.EnableSelection = true;
        _note.HorizontalAlignment = HorizontalAlignment.Stretch;
        _note.Padding = new Padding(1, 0, 1, 0);
        _note.Margin = new Margin(0, 1, 0, 0);
        AddControl(_note);

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
        _pause.Text = "Пауза";
        _pause.BackgroundColor = Color.Transparent;
        _pause.ForegroundColor = ToolCardStyle.Muted;
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            if (!_paused) ShowOutput();
            RenderStatus();
        };
        AddControl(_follow);
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
        _assessment.Update(update.Assessment);
        if (_status is not (ToolCallStatus.Classifying or ToolCallStatus.AwaitingApproval)) _approval?.Cancel();
        _result = update.Result;
        _execution = update.BashExecution ?? _execution;
        if (_status == ToolCallStatus.Running) _spinner.Start();
        else if (_status is ToolCallStatus.AwaitingApproval or ToolCallStatus.Classifying) _spinner.Stop();
        else StopActivity();
        if (!_paused) ShowOutput();
        RenderStatus();
    }

    public ExecutionApprovalComponent ShowApproval(Action<bool> complete)
    {
        IsExpanded = true;
        // Added only now: an always-present empty container would paint its own border line under the output.
        _approval = new ExecutionApprovalComponent(complete);
        AddControl(_approval);
        return _approval;
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
        if (_status is not (ToolCallStatus.Running or ToolCallStatus.AwaitingApproval or ToolCallStatus.Classifying)) return;
        _status = ToolCallStatus.Cancelled;
        _result = null;
        _approval?.Cancel();
        StopActivity();
        if (!_paused) ShowOutput();
        RenderStatus();
    }

    private void ShowOutput()
    {
        _viewport.Visible = _status is not (ToolCallStatus.Classifying or ToolCallStatus.AwaitingApproval);
        RenderNote();
        if (!_viewport.Visible) return;
        var text = _execution is null ? BashOutputText.Normalize(_result ?? "") : BashOutputText.Normalize(_execution.Output);
        if (text.Length == 0)
        {
            var placeholder = _status == ToolCallStatus.Running ? RunningPlaceholder : EmptyPlaceholder;
            if (_displayed == placeholder) return;
            _output.Text = $"[{ToolCardStyle.Muted.ToMarkup()}]{placeholder}[/]";
            _displayed = placeholder;
            return;
        }
        if (text == _displayed) return;
        // Append only when it is a true extension. CR progress and completion may replace text.
        if (_displayed.Length > 0 && text.StartsWith(_displayed, StringComparison.Ordinal))
            _output.AppendInline(MarkupParser.Escape(text[_displayed.Length..]));
        else _output.Text = MarkupParser.Escape(text);
        _displayed = text;
    }

    private void RenderNote()
    {
        var notes = new List<string>();
        if (_execution?.Truncated == true)
            notes.Add($"[{ToolCardStyle.Warning.ToMarkup()}]вывод ограничен 32 000 символами[/]");
        if (_status == ToolCallStatus.Failed && _execution is not null && _result is not null &&
            !_result.StartsWith("Error: Exit code:", StringComparison.Ordinal))
            notes.Add($"[{ToolCardStyle.Error.ToMarkup()}]{MarkupParser.Escape(BashOutputText.Normalize(_result).Trim())}[/]");
        _note.Text = string.Join("\n", notes);
        _note.Visible = notes.Count > 0;
    }

    private void RenderStatus()
    {
        if (_disposed) return;
        var (label, color) = ToolCardStyle.Status(_status);
        var seconds = _execution?.ElapsedSeconds ?? _finishedSeconds ?? Stopwatch.GetElapsedTime(_created).TotalSeconds;
        if (_status == ToolCallStatus.Running && _execution is not null)
            seconds += Math.Max(0, (DateTimeOffset.UtcNow - _execution.StartedAt).TotalSeconds - _execution.ElapsedSeconds);
        var exit = _status != ToolCallStatus.Running && _execution?.ExitCode is { } code
            ? $" · [{(code == 0 ? "green" : "red")}]exit {code}[/]"
            : "";
        Title = $"[bold]Bash[/] · [{color}]{label}{(_status == ToolCallStatus.Running && _frame.Length > 0 ? " " + _frame : "")}[/]" +
            (_execution is null ? "" : $" · [grey70]{ToolCardStyle.Duration(seconds)}[/]") + exit;
        _follow.Visible = _paused || !_viewport.AutoScroll;
        _pause.Visible = _status == ToolCallStatus.Running || _paused;
        _pause.Text = _paused ? "Продолжить" : "Пауза";
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
