using System.Text.Json;
using LakeCode.Core.Agent;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace LakeCode.Cli.Components;

internal sealed class EvalToolCallComponent : CollapsiblePanel
{
    private const int ResultLineLimit = 8;
    private const int ResultCharacterLimit = 2000;
    private readonly MarkupControl _code;
    private readonly MarkupControl _result;
    private readonly string _options;
    private ToolCallStatus _status;

    public EvalToolCallComponent(ToolCallUpdate update)
    {
        Collapsible = false;
        ShowHeader = true;
        HeaderStyle = CollapsibleHeaderStyle.Rounded;
        ExpandedIcon = "";
        Padding = new Padding(1, 1, 1, 1);
        BorderColor = new Color(85, 85, 85);
        ForegroundColor = new Color(185, 185, 185);
        IsExpanded = true;
        MaxContentHeight = int.MaxValue;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 0, 0, 1);
        BackgroundColor = new Color(26, 26, 26);

        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        var code = root.TryGetProperty("code", out var value) ? value.GetString() ?? "" : "";
        _options = string.Join(" · ", root.EnumerateObject()
            .Where(property => property.Name != "code")
            .Select(property => $"{property.Name}: {property.Value}"));

        _code = CreateBody();
        _code.Margin = new Margin(0, 0, 0, 1);
        // Convert directly so a literal markup closing tag in C# cannot end a Markdown region.
        var fence = "```";
        while (code.Contains(fence, StringComparison.Ordinal)) fence += "`";
        _code.Text = MarkdownToMarkup.Convert($"{fence}csharp\n{code}\n{fence}");
        _result = CreateBody();
        AddControl(_code);
        AddControl(_result);
        Update(update);
    }

    public void Update(ToolCallUpdate update)
    {
        _status = update.Status;
        var result = update.Result ?? (update.Status == ToolCallStatus.Running
            ? "Running…" : "No result received.");
        ShowResult(result);
    }

    public void FinishPending()
    {
        if (_status != ToolCallStatus.Running) return;
        _status = ToolCallStatus.Cancelled;
        ShowResult("The request ended before a result was received.");
    }

    private void ShowResult(string result)
    {
        var (label, color) = _status switch
        {
            ToolCallStatus.Running => ("running", "grey"),
            ToolCallStatus.Completed => ("done", "green"),
            ToolCallStatus.Failed => ("failed", "red"),
            ToolCallStatus.Cancelled => ("cancelled", "yellow"),
            _ => throw new ArgumentOutOfRangeException(nameof(_status))
        };
        var options = string.IsNullOrEmpty(_options) ? "" : $" · {MarkupParser.Escape(_options)}";
        Title = $"[bold]eval · C#[/] · [{color}]{label}[/]";
        _result.Text = $"[bold]Результат[/]{options}\n{MarkupParser.Escape(PreviewResult(result))}";
    }

    private static string PreviewResult(string result)
    {
        result = result.Replace("\r\n", "\n").Replace('\r', '\n');
        var length = Math.Min(result.Length, ResultCharacterLimit);
        var lines = 1;
        for (var index = 0; index < length; index++)
        {
            if (result[index] == '\n' && ++lines > ResultLineLimit)
            {
                length = index;
                break;
            }
        }
        if (length == result.Length) return result;
        if (length > 0 && char.IsHighSurrogate(result[length - 1])) length--;
        return $"{result[..length]}\n… результат обрезан (до {ResultLineLimit} строк, {ResultCharacterLimit} символов).";
    }

    private MarkupControl CreateBody() => new(new List<string>())
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Wrap = true,
        EnableSelection = true,
        BackgroundColor = BackgroundColor,
        ForegroundColor = new Color(185, 185, 185)
    };
}
