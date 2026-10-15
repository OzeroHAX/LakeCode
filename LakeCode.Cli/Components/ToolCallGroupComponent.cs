using System.Text.Json;
using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Delegation;
using SharpConsoleUI.Configuration;
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
        public string StatusLabel { get; set; } = "running";
        public DelegationStatus? DelegationStatus { get; set; }
        public ExternalReadPermissionComponent? Permission { get; set; }
        public ScrollablePanelControl? Viewport { get; set; }
        public ButtonControl? ExecutePlan { get; set; }
        public Action? ExecutePlanAction { get; set; }
    }

    private readonly Dictionary<string, Entry> _calls = new(StringComparer.Ordinal);
    private readonly bool _alwaysVisible;
    private readonly Dictionary<string, Entry> _delegations = new(StringComparer.Ordinal);
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
        if (!_calls.TryGetValue(update.CallId, out var entry) && update.Delegation is { } resumed &&
            _delegations.TryGetValue(resumed.TaskId, out entry))
            _calls.Add(update.CallId, entry);
        if (entry is null)
        {
            var panel = new CollapsiblePanel
            {
                HeaderStyle = _alwaysVisible ? CollapsibleHeaderStyle.Rounded : CollapsibleHeaderStyle.Borderless,
                Padding = _alwaysVisible ? new Padding(1, 1, 1, 1) : new Padding(0, 0, 0, 0),
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
                Margin = _alwaysVisible ? new Margin(0, 0, 0, 0) : new Margin(2, 1, 2, 1),
                EnableSelection = _alwaysVisible
            };
            if (_alwaysVisible)
            {
                panel.ExpandedIcon = "";
                panel.BorderColor = new Color(85, 85, 85);
                panel.BackgroundColor = BackgroundColor;
                panel.ForegroundColor = new Color(185, 185, 185);
                panel.FocusedForegroundColor = new Color(185, 185, 185);
                panel.FocusedBackgroundColor = new Color(45, 45, 45);
                body.BackgroundColor = BackgroundColor;
            }
            if (!_alwaysVisible)
            {
                body.Border = BorderStyle.Rounded;
                body.BorderColor = new Color(85, 85, 85);
                body.Padding = new Padding(1, 1, 1, 1);
                body.Margin = new Margin(2, 1, 0, 1);
                body.BackgroundColor = new Color(26, 26, 26);
                body.ForegroundColor = new Color(185, 185, 185);
                body.EnableSelection = true;
            }
            ScrollablePanelControl? viewport = null;
            if (ToolName == "delegate")
            {
                panel.MaxContentHeight = ControlDefaults.ScrollablePanelDefaultUnboundedHeight;
                viewport = new ScrollablePanelControl
                {
                    BorderStyle = BorderStyle.None,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ShowScrollbar = true,
                    BackgroundColor = BackgroundColor
                };
                viewport.AddControl(body);
                panel.AddControl(viewport);
            }
            else panel.AddControl(body);
            var summary = _alwaysVisible ? $"{ToolName}: {Summarize(update)}" : Summarize(update);
            entry = new Entry(summary, FormatArguments(update), panel, body) { Viewport = viewport };
            if (update.Delegation is { } delegation) _delegations.Add(delegation.TaskId, entry);
            _calls.Add(update.CallId, entry);
            AddControl(panel);
        }
        entry.Status = update.Status;
        entry.DelegationStatus = update.Delegation?.Status;
        if (update.Status != ToolCallStatus.Running && entry.DelegationStatus is not (DelegationStatus.Running or DelegationStatus.WaitingForParent))
        {
            entry.Permission?.Cancel();
            entry.Body.Visible = true;
            if (entry.Viewport is { } viewport) viewport.Visible = true;
        }
        var statusLabel = update.Delegation is { } state ? DelegationLabel(state.Status) : StatusLabel(update.Status);
        entry.StatusLabel = statusLabel;
        entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · {statusLabel}");
        if (!_alwaysVisible)
            entry.Body.Header = MarkupParser.Escape($"{ToolName}: {entry.Summary} · {StatusLabel(update.Status)}");
        entry.Body.Text = update.Delegation is { } research
            ? MarkupParser.Escape(FormatDelegation(entry.Arguments, research))
            : update.Status == ToolCallStatus.Completed && update.FileChange is { } change
            ? FileDiffPreview.Format(change)
            : MarkupParser.Escape(FormatBody(entry.Arguments, update.Result ?? "Running…"));
        if (ToolName == "submit_plan" && update.Status == ToolCallStatus.Completed)
        {
            using var planArgs = JsonDocument.Parse(update.Arguments);
            entry.Panel.Title = "[bold]План для рассмотрения[/]";
            entry.Body.SetMarkdown(planArgs.RootElement.GetProperty("markdown").GetString()!);
        }
        UpdateTitle();
    }

    public void SetPlanAction(string callId, ImplementationPlan? plan, bool enabled, Action? execute)
    {
        if (ToolName != "submit_plan" || !_calls.TryGetValue(callId, out var entry)) return;
        if (entry.ExecutePlan is null && plan is not null)
        {
            var button = new ButtonControl
            {
                BackgroundColor = BackgroundColor,
                ForegroundColor = new Color(155, 195, 205),
                FocusedBackgroundColor = new Color(35, 65, 75),
                Margin = new Margin(0, 1, 0, 0)
            };
            button.Click += (_, _) => entry.ExecutePlanAction?.Invoke();
            entry.ExecutePlan = button;
            entry.Panel.AddControl(button);
        }
        entry.ExecutePlanAction = enabled ? execute : null;
        if (entry.ExecutePlan is not { } control) return;
        control.Visible = plan is not null;
        control.IsEnabled = enabled;
        control.Text = $"Принять и реализовать план v{plan?.Revision}";
    }

    public void ShowReadPermission(ExternalReadRequest request, Action<ExternalReadDecision> complete,
        Window window)
    {
        var entry = _calls[request.CallId];
        IsExpanded = true;
        entry.Panel.IsExpanded = true;
        entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · ожидает разрешения");
        entry.Body.Visible = false;
        if (entry.Viewport is { } viewport) viewport.Visible = false;
        var permission = new ExternalReadPermissionComponent(request, complete);
        entry.Permission = permission;
        entry.Panel.AddControl(permission);
        permission.Focus(window);
    }

    public void CancelReadPermission(string callId)
    {
        if (!_calls.TryGetValue(callId, out var entry)) return;
        entry.Permission?.Cancel();
        if (ToolName != "delegate") return;
        entry.Body.Visible = true;
        if (entry.Viewport is { } viewport) viewport.Visible = true;
        entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · {entry.StatusLabel}");
    }

    public void FinishPending()
    {
        foreach (var entry in _calls.Values.Distinct())
        {
            if (entry.Status != ToolCallStatus.Running ||
                entry.DelegationStatus is DelegationStatus.Running or DelegationStatus.WaitingForParent) continue;
            entry.Permission?.Cancel();
            entry.Status = ToolCallStatus.Cancelled;
            entry.Panel.Title = MarkupParser.Escape($"{entry.Summary} · interrupted");
            if (!_alwaysVisible)
                entry.Body.Header = MarkupParser.Escape($"{ToolName}: {entry.Summary} · interrupted");
            entry.Body.Text = MarkupParser.Escape(FormatBody(entry.Arguments, "The request ended before a result was received."));
        }
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var entries = _calls.Values.Distinct().ToArray();
        var count = entries.Length;
        var summary = ToolName switch
        {
            "delegate" => $"Research {count} {(count == 1 ? "task" : "tasks")}",
            "wait_subagents" => $"Wait {count} {(count == 1 ? "time" : "times")}",
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
        var running = entries.Count(entry => entry.Status == ToolCallStatus.Running);
        var failed = entries.Count(entry => entry.Status == ToolCallStatus.Failed);
        var cancelled = entries.Count(entry => entry.Status == ToolCallStatus.Cancelled);
        if (running != 0) summary += $" · {running} running";
        if (failed != 0) summary += $" · {failed} failed";
        if (cancelled != 0) summary += $" · {cancelled} cancelled";
        Title = MarkupParser.Escape(summary);
    }

    private static string DelegationLabel(DelegationStatus status) => status switch
    {
        DelegationStatus.Running => "исследует",
        DelegationStatus.WaitingForParent => "ожидает решения основного агента",
        DelegationStatus.Completed => "завершено",
        DelegationStatus.Failed => "ошибка",
        DelegationStatus.Cancelled => "отменено",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static string FormatDelegation(string arguments, DelegationSnapshot snapshot)
    {
        var sections = new List<string> { arguments, $"Задача: {snapshot.TaskId}\nСтатус: {DelegationLabel(snapshot.Status)}" };
        if (snapshot.Question is not null) sections.Add($"Вопрос: {snapshot.Question}\nПричина: {snapshot.Reason}");
        if (snapshot.Answer is not null) sections.Add($"Ответ основного агента: {snapshot.Answer}");
        if (snapshot.Result is not null) sections.Add($"Результат:\n{snapshot.Result}");
        if (snapshot.Error is not null) sections.Add($"Ошибка: {snapshot.Error}");
        return string.Join("\n\n", sections.Where(section => !string.IsNullOrEmpty(section)));
    }

    private static string StatusLabel(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Running => "running",
        ToolCallStatus.AwaitingApproval => "ожидает подтверждения",
        ToolCallStatus.Completed => "done",
        ToolCallStatus.Failed => "failed",
        ToolCallStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    internal static string FormatArguments(ToolCallUpdate update)
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

    internal static string Summarize(ToolCallUpdate update)
    {
        if (update.Delegation is { } research)
        {
            var title = research.Task.Split('\n')[0];
            return title.Length > 120 ? title[..117] + "…" : title;
        }
        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        if (update.Name == "wait_subagents")
            return root.TryGetProperty("taskIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? $"Ожидание {ids.GetArrayLength()} исследований" : "Ожидание фоновых исследований";
        if (update.Name == "delegate")
        {
            if (root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.String)
            {
                var title = task.GetString()!.Split('\n')[0];
                return title.Length > 120 ? title[..117] + "…" : title;
            }
            return root.TryGetProperty("taskId", out var taskId)
                ? $"Продолжение {taskId}" : "Исследование";
        }
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
