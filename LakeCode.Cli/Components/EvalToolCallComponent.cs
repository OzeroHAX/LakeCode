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
    private const double DefaultTimeoutSeconds = 30;

    private readonly MarkupControl _code;
    private readonly MarkupControl _parameters;
    private readonly MarkupControl _result;
    private readonly ExecutionAssessmentComponent _assessment = new();
    private ExecutionApprovalComponent? _approval;
    private ToolCallStatus _status;

    public EvalToolCallComponent(ToolCallUpdate update)
    {
        Collapsible = false;
        ShowHeader = true;
        HeaderStyle = CollapsibleHeaderStyle.Rounded;
        ExpandedIcon = "";
        Padding = new Padding(1, 1, 1, 1);
        BorderColor = ToolCardStyle.Border;
        ForegroundColor = ToolCardStyle.Output;
        IsExpanded = true;
        MaxContentHeight = int.MaxValue;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Margin(0, 0, 0, 1);
        BackgroundColor = ToolCardStyle.Surface;

        using var arguments = JsonDocument.Parse(update.Arguments);
        var root = arguments.RootElement;
        var described = root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String;
        if (described)
        {
            var explanation = CreateBody(ToolCardStyle.Description);
            explanation.Text = MarkupParser.Escape(description.GetString() ?? "");
            AddControl(explanation);
        }

        var code = root.TryGetProperty("code", out var value) ? value.GetString() ?? "" : "";
        _code = CreateBody(ToolCardStyle.Command);
        _code.BackgroundColor = ToolCardStyle.CommandSurface;
        _code.Margin = new Margin(0, described ? 1 : 0, 0, 1);
        // Convert directly so a literal markup closing tag in C# cannot end a Markdown region.
        var fence = "```";
        while (code.Contains(fence, StringComparison.Ordinal)) fence += "`";
        _code.Text = MarkdownToMarkup.Convert($"{fence}csharp\n{code}\n{fence}");
        AddControl(_code);

        // Only non-default options are worth a line; the defaults are the tool's contract, not news.
        var parameters = root.EnumerateObject()
            .Where(property => property.Name is not ("code" or "description"))
            .Where(property => !IsDefaultParameter(property))
            .Select(FormatParameter)
            .ToList();
        _parameters = CreateBody(ToolCardStyle.Muted);
        _parameters.Text = MarkupParser.Escape("параметры: " + string.Join(" · ", parameters));
        _parameters.Visible = parameters.Count > 0;
        AddControl(_parameters);
        AddControl(_assessment);

        _result = CreateBody(ToolCardStyle.Output);
        _result.Margin = new Margin(0, 1, 0, 0);
        AddControl(_result);
        Update(update);
    }

    public void Update(ToolCallUpdate update)
    {
        _status = update.Status;
        _assessment.Update(update.Assessment);
        if (_status is not (ToolCallStatus.Classifying or ToolCallStatus.AwaitingApproval)) _approval?.Cancel();
        ShowResult(update.Result);
    }

    public ExecutionApprovalComponent ShowApproval(Action<bool> complete)
    {
        IsExpanded = true;
        // Added only now: an always-present empty container would paint its own border line under the output.
        _approval = new ExecutionApprovalComponent(complete);
        AddControl(_approval);
        return _approval;
    }

    public void FinishPending()
    {
        if (_status is not (ToolCallStatus.Running or ToolCallStatus.AwaitingApproval or ToolCallStatus.Classifying)) return;
        _status = ToolCallStatus.Cancelled;
        _approval?.Cancel();
        ShowResult(null);
    }

    private void ShowResult(string? result)
    {
        var (label, color) = ToolCardStyle.Status(_status);
        Title = $"[bold]eval · C#[/] · [{color}]{label}[/]";
        _result.Visible = _status is not (ToolCallStatus.Classifying or ToolCallStatus.AwaitingApproval);
        if (!_result.Visible) return;
        if (_status == ToolCallStatus.Running)
        {
            _result.ForegroundColor = ToolCardStyle.Muted;
            _result.Text = "выполняется…";
            return;
        }
        _result.ForegroundColor = _status switch
        {
            ToolCallStatus.Failed => ToolCardStyle.Error,
            ToolCallStatus.Cancelled => ToolCardStyle.Muted,
            _ => ToolCardStyle.Output
        };
        var (preview, truncated) = PreviewResult(result ?? "результат не получен");
        _result.Text = $"[{ToolCardStyle.Muted.ToMarkup()}]результат[/]\n{MarkupParser.Escape(preview)}" +
            (truncated
                ? $"\n[{ToolCardStyle.Muted.ToMarkup()}]обрезано до {ResultLineLimit} строк / {ResultCharacterLimit} символов[/]"
                : "");
    }

    private static (string Text, bool Truncated) PreviewResult(string result)
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
        if (length == result.Length) return (result, false);
        if (length > 0 && char.IsHighSurrogate(result[length - 1])) length--;
        return (result[..length], true);
    }

    private static bool IsDefaultParameter(JsonProperty property) => property.Name switch
    {
        "timeout" => property.Value.ValueKind == JsonValueKind.Number &&
                     property.Value.TryGetDouble(out var seconds) && Math.Abs(seconds - DefaultTimeoutSeconds) < 0.001,
        "reset" => property.Value.ValueKind == JsonValueKind.False,
        _ => false
    };

    private static string FormatParameter(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.True => $"{property.Name}: true",
        JsonValueKind.False => $"{property.Name}: false",
        JsonValueKind.String => $"{property.Name}: {property.Value.GetString()}",
        _ => $"{property.Name}: {property.Value}"
    };

    private MarkupControl CreateBody(Color color) => new(new List<string>())
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Wrap = true,
        EnableSelection = true,
        BackgroundColor = BackgroundColor,
        ForegroundColor = color
    };
}
