using System.Text.Json;
using LakeCode.Core.Agent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class ExecutionApprovalComponent : CollapsiblePanel
{
    private readonly ListControl _choices;
    private readonly MarkupControl _status;
    private readonly Action<bool> _complete;
    private bool _finished;

    public ExecutionApprovalComponent(ExecutionApprovalRequest request, Action<bool> complete)
    {
        _complete = complete;
        Title = $"{request.ToolName} · разрешение на один вызов";
        IsExpanded = true;
        Collapsible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 1, 0, 1);
        AddControl(new MarkupControl(new List<string>
        {
            MarkupParser.Escape(request.Description),
            "[yellow]Описание дано моделью, не проверкой безопасности. Код выполняется с вашими правами; это не sandbox.[/]"
        }) { Wrap = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        using var args = JsonDocument.Parse(request.Arguments);
        var text = string.Join("\n\n", args.RootElement.EnumerateObject()
            .Where(p => p.Name != "description").Select(p => $"{p.Name}:\n{p.Value}"));
        if (request.ToolName == "bash" && !args.RootElement.TryGetProperty("workingDirectory", out _))
            text = "workingDirectory: . (корень проекта)\n\n" + text;
        if (request.ToolName == "eval")
        {
            if (!args.RootElement.TryGetProperty("timeout", out _)) text += "\n\ntimeout: 30 секунд";
            if (!args.RootElement.TryGetProperty("reset", out _)) text += "\n\nreset: false";
        }
        var viewport = new ScrollablePanelControl
        {
            Height = 12, HorizontalAlignment = HorizontalAlignment.Stretch, ShowScrollbar = true,
            BorderStyle = BorderStyle.Rounded
        };
        viewport.AddControl(new MarkupControl(new List<string> { MarkupParser.Escape(text) })
        { Wrap = true, EnableSelection = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        AddControl(viewport);
        _status = new MarkupControl(new List<string> { "[yellow]Ожидает решения. Разрешение не утверждает план и не включает Build. Esc дважды — остановить запрос.[/]" }) { Wrap = true };
        AddControl(_status);
        _choices = new ListControl { IsSelectable = true, MaxVisibleItems = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        _choices.AddItem(new ListItem("Отклонить") { Tag = false });
        _choices.AddItem(new ListItem("Разрешить один раз") { Tag = true });
        _choices.SelectedIndex = 0;
        _choices.ItemActivated += (_, _) =>
        {
            if (_finished || _choices.SelectedItem?.Tag is not bool allow) return;
            _finished = true;
            _choices.Visible = false;
            _status.Text = allow ? "[green]Этот вызов разрешён.[/]" : "[yellow]Вызов отклонён, действие не выполнено.[/]";
            _complete(allow);
        };
        AddControl(_choices);
    }

    public void Focus(Window window) => window.FocusControl(_choices);
    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _choices.Visible = false;
        _status.Text = "[grey70]Ожидание отменено. Разрешение не выдано.[/]";
    }
}