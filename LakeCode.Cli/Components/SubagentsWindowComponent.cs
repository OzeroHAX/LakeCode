using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Delegation;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

/// <summary>Read-only transcripts. Focus and lifetime are independent of child execution.</summary>
internal sealed class SubagentsWindowComponent(ConsoleWindowSystem windowSystem) : IDisposable
{
    private sealed class Block(IWindowControl control)
    {
        public readonly IWindowControl Control = control;
        public int Characters;
        public string? CallId;
        public bool Pending;
    }

    private sealed class TaskView(DelegationSnapshot snapshot)
    {
        public DelegationSnapshot Snapshot = snapshot;
        public readonly MainContentComponent Transcript = new() { Visible = false };
        public readonly List<Block> Blocks = [];
        public readonly Dictionary<string, Block> Tools = new(StringComparer.Ordinal);
        public AssistantMessageComponent? Response;
        public Block? ResponseBlock;
        public bool Historical;
        public bool Truncated;
    }

    private readonly Dictionary<string, TaskView> _tasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exchanges = new(StringComparer.Ordinal);
    private Window? _window, _parent;
    private ListControl? _list;
    private PanelControl? _transcripts;
    private MarkupControl? _status;
    private string? _selectedId;
    private bool _interactionActive;

    public void SetInteractionActive(bool active)
    {
        _interactionActive = active;
        if (_window is { } window) window.AlwaysOnTop = !active;
    }
    public event Action? ReturnToChat;
    public IReadOnlyCollection<DelegationSnapshot> Tasks => _tasks.Values.Select(item => item.Snapshot).ToArray();

    public (string Kind, string Text)? Apply(DelegationSnapshot snapshot, bool historical = false)
    {
        if (!_tasks.TryGetValue(snapshot.TaskId, out var view))
        {
            _tasks.Add(snapshot.TaskId, view = new TaskView(snapshot));
            _transcripts?.AddControl(view.Transcript);
            if (historical)
                AddBlock(view, new UserMessageComponent(snapshot.Task +
                    (string.IsNullOrWhiteSpace(snapshot.Context) ? "" : "\n\nКонтекст:\n" + snapshot.Context), []),
                    snapshot.Task.Length + (snapshot.Context?.Length ?? 0));
        }
        view.Snapshot = snapshot;
        view.Historical = historical;
        var exchange = ProjectExchange(snapshot, _exchanges);
        if (exchange is { } item)
        {
            if (snapshot.Status == DelegationStatus.Running)
                AddBlock(view, new UserMessageComponent(item.Text, []), item.Text.Length);
            else if (snapshot.Status is DelegationStatus.Failed or DelegationStatus.Cancelled)
            {
                EndMessage(view);
                var error = NewResponse();
                error.ShowError(item.Text);
                AddBlock(view, error, item.Text.Length);
            }
            else if (snapshot.Status == DelegationStatus.WaitingForParent)
                AddBlock(view, new DelegationMessageComponent(snapshot, item.Kind, item.Text), item.Text.Length);
            else if (historical && snapshot.Result is { } result)
            {
                var answer = NewResponse();
                answer.AppendResponse(result);
                answer.Complete();
                AddBlock(view, answer, result.Length);
            }
        }
        RenderList();
        RenderStatus();
        return exchange;
    }

    public static (string Kind, string Text)? ProjectExchange(DelegationSnapshot snapshot, HashSet<string> exchanges)
    {
        var key = $"{snapshot.TaskId}:{snapshot.RequestNumber}:{snapshot.Status}";
        if (!exchanges.Add(key)) return null;
        return snapshot.Status switch
        {
            DelegationStatus.Running when snapshot.RequestNumber == 1 => ("Запрос",
                snapshot.Task + (string.IsNullOrWhiteSpace(snapshot.Context) ? "" : "\n\nКонтекст:\n" + snapshot.Context)),
            DelegationStatus.Running => ("Ответ основного агента", snapshot.Answer ?? "Продолжение исследования"),
            DelegationStatus.WaitingForParent => ("Вопрос", snapshot.Question + "\n\n" + snapshot.Reason),
            DelegationStatus.Completed => ("Ответ", snapshot.Result ?? ""),
            DelegationStatus.Failed => ("Ошибка", snapshot.Error ?? "Исследование завершилось с ошибкой."),
            DelegationStatus.Cancelled => ("Отмена", snapshot.Error ?? "Исследование отменено."),
            _ => null
        };
    }

    private AssistantMessageComponent NewResponse()
    {
        var response = new AssistantMessageComponent(windowSystem, readOnly: true);
        response.SuspendForDelegation();
        return response;
    }

    public void Append(DelegationActivity activity)
    {
        if (!_tasks.TryGetValue(activity.TaskId, out var view)) return;
        if (activity.Kind == "request") return; // Friendly request already projected from the snapshot.
        if (activity.Kind == "message_end") EndMessage(view);
        else if (activity.ToolCall is { } call)
        {
            EndMessage(view);
            if (!view.Tools.TryGetValue(call.CallId, out var block))
            {
                block = AddBlock(view, NewResponse(), 0);
                block.CallId = call.CallId;
                view.Tools.Add(call.CallId, block);
            }
            ((AssistantMessageComponent)block.Control).UpdateToolCall(call);
            block.Pending = call.Status == ToolCallStatus.Running;
            block.Characters = call.Arguments.Length + (call.Result?.Length ?? 0);
        }
        else if (activity.Text is { Length: > 0 } text)
        {
            if (view.Response is null)
            {
                view.Response = NewResponse();
                view.ResponseBlock = AddBlock(view, view.Response, 0);
                view.ResponseBlock.Pending = true;
            }
            if (activity.Kind == "thinking") view.Response.AppendReasoning(text);
            else if (activity.Kind == "text")
            {
                view.Response.AppendResponse(text);
            }
            view.ResponseBlock!.Characters += text.Length;
        }
        Trim(view);
        RenderStatus();
    }

    private static void EndMessage(TaskView view)
    {
        if (view.Response is null) return;
        view.Response.Complete();
        view.ResponseBlock!.Pending = false;
        view.Response = null;
        view.ResponseBlock = null;
    }

    private static Block AddBlock(TaskView view, IWindowControl control, int characters)
    {
        var block = new Block(control) { Characters = characters };
        view.Blocks.Add(block);
        view.Transcript.AddControl(control);
        Trim(view);
        return block;
    }

    private static void Trim(TaskView view)
    {
        // Keep coherent blocks, never cut a message or discard an in-flight tool.
        while (view.Blocks.Count > 1 && (view.Blocks.Count > 128 || view.Blocks.Sum(block => (long)block.Characters) > 64_000))
        {
            var oldest = view.Blocks[0];
            if (oldest.Pending) break;
            ((IControlHost)view.Transcript).RemoveControl(oldest.Control);
            view.Blocks.RemoveAt(0);
            if (oldest.CallId is { } id) view.Tools.Remove(id);
            view.Truncated = true;
        }
    }

    public void Show(Window parent, string? taskId = null)
    {
        if (_tasks.Count == 0) return;
        _parent = parent;
        if (_window is null)
        {
            _list = new ListControl
            {
                IsSelectable = true, MaxVisibleItems = 5, HorizontalAlignment = HorizontalAlignment.Stretch,
                HighlightBackgroundColor = new Color(35, 65, 75), HighlightForegroundColor = Color.White,
                Margin = new Margin(1, 1, 1, 0)
            };
            _list.ItemActivated += (_, _) =>
            {
                if (_list.SelectedItem?.Tag is not string id) return;
                Select(id);
                _window!.FocusControl(_tasks[id].Transcript);
            };
            _status = new MarkupControl([]) { Wrap = true, Margin = new Margin(2, 0, 2, 0) };
            _transcripts = new PanelControl
            { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill };
            foreach (var view in _tasks.Values) _transcripts.AddControl(view.Transcript);
            var back = new ButtonControl { Text = "Вернуться в чат", BackgroundColor = Color.Transparent,
                FocusedBackgroundColor = new Color(35, 65, 75) };
            back.Click += (_, _) => FocusChat();
            _window = new WindowBuilder(windowSystem)
                .WithTitle("Сабагенты")
                .WithSize(Math.Min(90, parent.Width), Math.Min(30, parent.Height))
                .Centered().Movable().Resizable().Minimizable(false).Maximizable(false)
                .WithBorderStyle(BorderStyle.Rounded).WithBorderColor(new Color(80, 130, 145))
                .WithBackgroundColor(new Color(26, 26, 26)).WithForegroundColor(new Color(185, 185, 185))
                .AddControl(_list).AddControl(_status).AddControl(_transcripts).AddControl(back)
                .AddControl(new MarkupControl(["[grey70]Enter — открыть задачу · Esc — закрыть без отмены[/]"])
                { Wrap = true, Margin = new Margin(1, 0, 1, 0) }).Build();
            _window.AlwaysOnTop = !_interactionActive;
            _window.CloseOnDeactivate = false;
            _window.PreviewKeyPressed += OnKey;
            _window.OnClosing += (_, _) => { ReleaseWindow(); FocusChat(); };
            windowSystem.AddWindow(_window);
        }
        Select(taskId is not null && _tasks.ContainsKey(taskId) ? taskId : _selectedId ?? _tasks.Keys.First());
        RenderList();
        windowSystem.SetActiveWindow(_window);
        _window.FocusControl(taskId is null ? _list! : _tasks[_selectedId!].Transcript);
    }

    private void Select(string id)
    {
        _selectedId = id;
        foreach (var pair in _tasks) pair.Value.Transcript.Visible = pair.Key == id;
        RenderStatus();
    }

    private void RenderList()
    {
        if (_list is null) return;
        var selected = _list.SelectedItem?.Tag as string ?? _selectedId;
        _list.ClearItems();
        var ordered = _tasks.Values.OrderBy(item => item.Snapshot.Status switch
        { DelegationStatus.WaitingForParent => 0, DelegationStatus.Running => 1, DelegationStatus.Failed => 2, _ => 3 }).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var task = ordered[i].Snapshot;
            _list.AddItem(new ListItem(MarkupParser.Escape($"{DelegationMessageComponent.Short(task.Task, 54)} · {DelegationMessageComponent.StatusLabel(task.Status)}"))
            { Tag = task.TaskId });
            if (task.TaskId == selected) _list.SelectedIndex = i;
        }
    }

    private void RenderStatus()
    {
        if (_status is null || _selectedId is null || !_tasks.TryGetValue(_selectedId, out var view)) return;
        _status.Text = MarkupParser.Escape($"{DelegationMessageComponent.StatusLabel(view.Snapshot.Status)}" +
            (view.Truncated ? " · Начало ленты скрыто: удалены старые блоки." : "") +
            (view.Historical ? " · Внутренняя лента сохранённой сессии недоступна." : ""));
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (e.Handled || e.KeyInfo.Key != ConsoleKey.Escape) return;
        e.Handled = true;
        Close();
    }

    private void FocusChat()
    {
        if (_parent is { } parent) windowSystem.SetActiveWindow(parent);
        ReturnToChat?.Invoke();
    }

    private void ReleaseWindow()
    {
        if (_window is { } window) window.PreviewKeyPressed -= OnKey;
        if (_transcripts is not null)
            foreach (var view in _tasks.Values) ((IControlHost)_transcripts).RemoveControl(view.Transcript);
        _window = null;
        _list = null;
        _status = null;
        _transcripts = null;
    }

    private void Close()
    {
        if (_window is { } window) windowSystem.CloseWindow(window);
    }

    public void Reset()
    {
        Close();
        _tasks.Clear();
        _exchanges.Clear();
        _selectedId = null;
    }

    public void Dispose() => Close();
}
