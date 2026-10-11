using System.Text;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class AssistantMessageComponent : CollapsiblePanel
{
    private readonly StringBuilder _response = new();
    private readonly ThinkingComponent _thinking;
    private readonly MarkupControl _body = new(new List<string>());

    public AssistantMessageComponent(ConsoleWindowSystem windowSystem)
    {
        _thinking = new ThinkingComponent(windowSystem);
        Collapsible = false;
        ShowHeader = false;
        HeaderStyle = CollapsibleHeaderStyle.Borderless;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(3, 0, 3, 1);
        _body.HorizontalAlignment = HorizontalAlignment.Stretch;
        _body.Wrap = true;
        _body.Text = "[grey]Ожидание ответа…[/]";
        AddControl(_thinking);
        AddControl(_body);
    }

    public void AppendReasoning(string text)
    {
        _thinking.AppendReasoning(text);
        if (_response.Length == 0)
            _body.Visible = false;
        else
            _thinking.CollapseAutomatically();
    }

    public void AppendResponse(string text)
    {
        if (_response.Length == 0)
            _thinking.Complete();
        _response.Append(text);
        _body.Visible = true;
        _body.SetMarkdown(_response.ToString());
    }

    public void FinishThinking() => _thinking.Complete();

    public void Complete()
    {
        _thinking.Complete();
        if (_response.Length == 0)
        {
            _body.Visible = true;
            _body.Text = "[grey]Модель завершила ответ без текста.[/]";
        }
    }

    public void ShowError(string message)
    {
        _thinking.Complete();
        var error = new MarkupControl(new List<string>
        {
            $"[red]Ошибка: {MarkupParser.Escape(message)}[/]"
        })
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Wrap = true
        };
        if (_response.Length == 0)
            _body.Visible = false;
        AddControl(error);
    }
}
