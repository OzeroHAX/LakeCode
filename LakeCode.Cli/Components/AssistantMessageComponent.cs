using System.Text;
using LakeCode.Core.Agent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class AssistantMessageComponent : CollapsiblePanel
{
    private readonly StringBuilder _response = new();
    private readonly ThinkingComponent _thinking;
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly Dictionary<string, BashToolCallComponent> _bashCalls = new(StringComparer.Ordinal);
    private MarkupControl? _body;
    private bool _hasResponse;
    private ToolCallGroupComponent? _activeToolGroup;
    private readonly Dictionary<string, ToolCallGroupComponent> _toolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvalToolCallComponent> _evalCalls = new(StringComparer.Ordinal);

    public AssistantMessageComponent(ConsoleWindowSystem windowSystem)
    {
        _windowSystem = windowSystem;
        _thinking = new ThinkingComponent(windowSystem);
        Collapsible = false;
        ShowHeader = false;
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(3, 0, 3, 1);
        _body = CreateBody();
        _body.HorizontalAlignment = HorizontalAlignment.Stretch;
        _body.Wrap = true;
        _body.Text = "[grey]Ожидание ответа…[/]";
        AddControl(_thinking);
        AddControl(_body);
    }

    public void AppendReasoning(string text)
    {
        _thinking.AppendReasoning(text);
        if (!_hasResponse && _body is not null)
            _body.Visible = false;
        else if (_hasResponse)
            _thinking.CollapseAutomatically();
    }

    public void AppendResponse(string text)
    {
        _thinking.Complete();
        _activeToolGroup = null;
        if (_body is null)
        {
            _body = CreateBody();
            AddControl(_body);
        }
        _hasResponse = true;
        _response.Append(text);
        _body.Visible = true;
        _body.SetMarkdown(_response.ToString());
    }

    public void UpdateToolCall(ToolCallUpdate update)
    {
        if (_bashCalls.TryGetValue(update.CallId, out var existingBash))
        {
            existingBash.Update(update);
            return;
        }
        if (_evalCalls.TryGetValue(update.CallId, out var existingEval))
        {
            existingEval.Update(update);
            return;
        }
        if (_toolCalls.TryGetValue(update.CallId, out var existing))
        {
            existing.Update(update);
            return;
        }

        _thinking.Complete();
        if (!_hasResponse && _body is not null)
            _body.Visible = false;
        _body = null;
        _response.Clear();
        if (update.Name == "eval")
        {
            _activeToolGroup = null;
            var call = new EvalToolCallComponent(update);
            _evalCalls.Add(update.CallId, call);
            AddControl(call);
            return;
        }
        if (update.Name == "bash")
        {
            _activeToolGroup = null;
            var call = new BashToolCallComponent(_windowSystem, update);
            _bashCalls.Add(update.CallId, call);
            AddControl(call);
            return;
        }
        if (update.Name is "write_file" or "edit_file")
        {
            _activeToolGroup = null;
            var call = new ToolCallGroupComponent(update.Name, alwaysVisible: true);
            _toolCalls.Add(update.CallId, call);
            AddControl(call);
            call.Update(update);
            return;
        }

        if (_activeToolGroup?.ToolName != update.Name)
        {
            _activeToolGroup = new ToolCallGroupComponent(update.Name);
            AddControl(_activeToolGroup);
        }
        _toolCalls.Add(update.CallId, _activeToolGroup);
        _activeToolGroup.Update(update);
    }

    public void UpdateBashOutput(BashOutputUpdate update)
    {
        if (_bashCalls.TryGetValue(update.CallId, out var call)) call.UpdateOutput(update.Execution);
    }

    public void ShowReadPermission(ExternalReadRequest request, Action<ExternalReadDecision> complete,
        Window window) => _toolCalls[request.CallId].ShowReadPermission(request, complete, window);

    public void CancelReadPermission(string callId)
    {
        if (_toolCalls.TryGetValue(callId, out var group)) group.CancelReadPermission(callId);
    }

    public void FinishThinking()
    {
        _thinking.Complete();
        foreach (var group in _toolCalls.Values.Distinct())
            group.FinishPending();
        foreach (var call in _evalCalls.Values)
            call.FinishPending();
        foreach (var call in _bashCalls.Values)
            call.FinishPending();
    }

    private static MarkupControl CreateBody() => new(new List<string>())
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Wrap = true,
        EnableSelection = true
    };

    public void Complete()
    {
        FinishThinking();
        if (!_hasResponse && _toolCalls.Count == 0 && _evalCalls.Count == 0 && _bashCalls.Count == 0)
        {
            _body!.Visible = true;
            _body.Text = "[grey]Модель завершила ответ без текста.[/]";
        }
    }

    public void ShowError(string message)
    {
        FinishThinking();
        var error = new MarkupControl(new List<string>
        {
            $"[red]Ошибка: {MarkupParser.Escape(message)}[/]"
        })
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Wrap = true
        };
        if (!_hasResponse && _body is not null)
            _body.Visible = false;
        AddControl(error);
    }
}
