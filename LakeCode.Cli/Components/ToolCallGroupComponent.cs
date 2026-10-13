using System.Text.Json;
using LakeCode.Core.Agent;
using SharpConsoleUI;
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
        public ExternalReadPermissionComponent? Permission { get; set; }
    }

    private readonly Dictionary<string, Entry> _calls = new(StringComparer.Ordinal);
    private readonly bool _alwaysVisible;
    public string ToolName { get; }

    public ToolCallGroupComponent(string toolName, bool alwaysVisible = false)
    {
        ToolName = toolName;
        _alwaysVisible = alwaysVisible;
        Collapsible = !alwaysVisible;
        ShowHeader = !alwaysVisible;
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 0, 0, 1);
        ForegroundColor = new Color(155, 155, 155);
        FocusedForegroundColor = new Color(185, 185, 185);
        FocusedBackgroundColor = new Color(45, 45, 45);
        IsExpanded = alwaysVisible;
        MaxContentHeight = int.MaxValue;
        if (alwaysVisible)
            BackgroundColor = new Color(26, 26, 26);
    }

    public void Update(ToolCallUpdate update)
    {
        if (!_calls.TryGetValue(update.CallId, out var entry))
        {
            var panel = new CollapsiblePanel
            {
                HeaderStyle = CollapsibleHeaderStyle.Borderless,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Collapsible = !_alwaysVisible,
                IsExpanded = _alwaysVisible,
                MaxContentHeight = int.MaxValue,
                Margin = new Margin(_alwaysVisible ? 0 : 2, 0, 0, 0)
            };
            var body = new MarkupControl(new List<string>())
            {
                Wrap = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = _alwaysVisible ? new Margin(0, 0, 0, 0) : new Margin(2, 1, 2, 1)
            };
            if (_alwaysVisible)
            {
                panel.BackgroundColor = BackgroundColor;
                body.BackgroundColor = BackgroundColor;
            }
            panel.AddControl(body);
            var summary = _alwaysVisible ? $"{ToolName}: {Summarize(update)}" : Summarize(update);
            entry = new Entry(summary, FormatArguments(update), panel, body);
            _calls.Add(update.CallId, entry);
            AddControl(panel);
        }
        entry.Status = update.Status;
        if (update.Status != ToolCallStatus.Running)
        {
            entry.Permission?.Cancel();
            entry.Body.Visible = true;
        }
        entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · {StatusLabel(update.Status)}");
        entry.Body.Text = update.Status == ToolCallStatus.Completed && update.FileChange is { } change
            ? FileDiffPreview.Format(change)
            : MarkupParser.Escape(FormatBody(entry.Arguments, update.Result ?? "Running…"));
        UpdateTitle();
    }

    public void ShowReadPermission(ExternalReadRequest request, Action<ExternalReadDecision> complete,
        Window window)
    {
        var entry = _calls[request.CallId];
        IsExpanded = true;
        entry.Panel.IsExpanded = true;
        entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · ожидает разрешения");
        entry.Body.Visible = false;
        var permission = new ExternalReadPermissionComponent(request, complete);
        entry.Permission = permission;
        entry.Panel.AddControl(permission);
        permission.Focus(window);
    }

    public void CancelReadPermission(string callId)
    {
        if (_calls.TryGetValue(callId, out var entry))
            entry.Permission?.Cancel();
    }

    public void FinishPending()
    {
        foreach (var entry in _calls.Values)
        {
            if (entry.Status != ToolCallStatus.Running) continue;
            entry.Permission?.Cancel();
            entry.Status = ToolCallStatus.Cancelled;
            entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · interrupted");
            entry.Body.Text = MarkupParser.Escape(FormatBody(entry.Arguments, "The request ended before a result was received."));
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
            "web_search" => $"Search {count} {(count == 1 ? "query" : "queries")}",
            "read_web" => $"Read {count} {(count == 1 ? "page" : "pages")}",
            "bash" => $"Run {count} {(count == 1 ? "command" : "commands")}",
            "eval" => $"Eval {count} C# {(count == 1 ? "script" : "scripts")}",
            "write_file" => $"Write {count} {(count == 1 ? "file" : "files")}",
            "edit_file" => $"Edit {count} {(count == 1 ? "file" : "files")}",
            "ask_tool" => $"Ask {count} {(count == 1 ? "question set" : "question sets")}",
            "load_skill" => $"Load {count} {(count == 1 ? "skill" : "skills")}",
            "read_skill_resource" => $"Read {count} skill {(count == 1 ? "resource" : "resources")}",
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

    private static string FormatArguments(ToolCallUpdate update)
    {
        using var arguments = JsonDocument.Parse(update.Arguments);
        return string.Join("\n", arguments.RootElement.EnumerateObject()
            .Where(property => update.Name != "bash" || property.Name != "command")
            .Where(property => update.Name is not ("edit_file" or "write_file") ||
                               property.Name is not ("oldText" or "newText" or "content"))
            .Select(property => $"{property.Name}: {property.Value}"));
    }

    private static string FormatBody(string arguments, string result) =>
        string.IsNullOrEmpty(arguments) ? result : $"{arguments}\n\n{result}";

    private static string Summarize(ToolCallUpdate update)
    {
        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        if (update.Name == "ask_tool" && root.TryGetProperty("questions", out var questions))
            return string.Join(" · ", questions.EnumerateArray().Select(question =>
                question.TryGetProperty("header", out var header) ? header.ToString() : "Question"));
        if (update.Name is "web_search" or "read_web")
        {
            var parameter = update.Name == "web_search" ? "query" : "url";
            return root.TryGetProperty(parameter, out var argument) && argument.ValueKind == JsonValueKind.String
                ? argument.GetString() ?? update.Name : update.Name;
        }
        if (update.Name is "load_skill" or "read_skill_resource" &&
            root.TryGetProperty("skillName", out var skill))
            return root.TryGetProperty("resourceName", out var resource)
                ? $"{skill}: {resource}" : skill.ToString();
        if (update.Name == "bash" && root.TryGetProperty("command", out var command))
            return command.ToString();
        var path = root.TryGetProperty("path", out var value) ? value.ToString() : ".";
        return update.Name == "grep" && root.TryGetProperty("pattern", out var pattern)
            ? $"{path}: {pattern}" : path;
    }
}
