using System.Text.Json;
using LakeCode.Cli.Agent;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class ToolCallGroupComponent : CollapsiblePanel
{
    private sealed class Entry(string summary, string arguments, CollapsiblePanel panel, MarkupControl body)
    {
        public string Summary { get; } = summary;
        public string Arguments { get; } = arguments;
        public CollapsiblePanel Panel { get; } = panel;
        public MarkupControl Body { get; } = body;
        public ToolCallStatus Status { get; set; } = ToolCallStatus.Running;
    }

    private readonly Dictionary<string, Entry> _calls = new(StringComparer.Ordinal);
    public string ToolName { get; }

    public ToolCallGroupComponent(string toolName)
    {
        ToolName = toolName;
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 0, 0, 1);
        ForegroundColor = new Color(155, 155, 155);
        FocusedForegroundColor = new Color(185, 185, 185);
        FocusedBackgroundColor = new Color(45, 45, 45);
        IsExpanded = false;
    }

    public void Update(ToolCallUpdate update)
    {
        if (!_calls.TryGetValue(update.CallId, out var entry))
        {
            var panel = new CollapsiblePanel
            {
                HeaderStyle = CollapsibleHeaderStyle.Borderless,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsExpanded = false,
                MaxContentHeight = ControlDefaults.ScrollablePanelDefaultUnboundedHeight,
                Margin = new Margin(2, 0, 0, 0)
            };
            var body = new MarkupControl(new List<string>())
            {
                Wrap = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var viewport = new ScrollablePanelControl
            {
                BorderStyle = BorderStyle.None,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Padding(2, 1, 2, 1),
                ShowScrollbar = true
            };
            viewport.AddControl(body);
            panel.AddControl(viewport);
            entry = new Entry(Summarize(update), update.Arguments, panel, body);
            _calls.Add(update.CallId, entry);
            AddControl(panel);
        }
        entry.Status = update.Status;
        entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · {StatusLabel(update.Status)}");
        entry.Body.Text = MarkupParser.Escape($"Arguments: {entry.Arguments}\n\n{update.Result ?? "Running…"}");
        UpdateTitle();
    }

    public void FinishPending()
    {
        foreach (var entry in _calls.Values)
        {
            if (entry.Status != ToolCallStatus.Running) continue;
            entry.Status = ToolCallStatus.Cancelled;
            entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · interrupted");
            entry.Body.Text = MarkupParser.Escape($"Arguments: {entry.Arguments}\n\nThe request ended before a result was received.");
        }
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var count = _calls.Count;
        var summary = ToolName switch
        {
            "read_file" => $"Read {count} {(count == 1 ? "file" : "files")}",
            "list_directory" => $"List {count} {(count == 1 ? "directory" : "directories")}",
            "grep" => $"Grep {count} {(count == 1 ? "search" : "searches")}",
            "bash" => $"Run {count} {(count == 1 ? "command" : "commands")}",
            "write_file" => $"Write {count} {(count == 1 ? "file" : "files")}",
            "edit_file" => $"Edit {count} {(count == 1 ? "file" : "files")}",
            _ => $"{ToolName} · {count} calls"
        };
        var running = _calls.Values.Count(entry => entry.Status == ToolCallStatus.Running);
        var failed = _calls.Values.Count(entry => entry.Status == ToolCallStatus.Failed);
        var cancelled = _calls.Values.Count(entry => entry.Status == ToolCallStatus.Cancelled);
        if (running != 0) summary += $" · {running} running";
        if (failed != 0) summary += $" · {failed} failed";
        if (cancelled != 0) summary += $" · {cancelled} cancelled";
        Title = MarkupParser.Escape(summary);
    }

    private static string StatusLabel(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Running => "running",
        ToolCallStatus.Completed => "done",
        ToolCallStatus.Failed => "failed",
        ToolCallStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static string Summarize(ToolCallUpdate update)
    {
        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        if (update.Name == "bash" && root.TryGetProperty("command", out var command))
            return command.ToString();
        var path = root.TryGetProperty("path", out var value) ? value.ToString() : ".";
        return update.Name == "grep" && root.TryGetProperty("pattern", out var pattern)
            ? $"{path}: {pattern}" : path;
    }
}
