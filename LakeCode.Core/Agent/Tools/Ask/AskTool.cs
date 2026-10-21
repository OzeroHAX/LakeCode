using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LakeCode.Core.Agent.Tools.Ask;

public sealed record AskOption(
    [property: JsonPropertyName("label"), Description("Short option label; put the recommended option first.")] string Label,
    [property: JsonPropertyName("description"), Description("Brief explanation of the option.")] string Description);

public sealed record AskQuestion(
    [property: JsonPropertyName("question"), Description("Concise question in the user's language.")] string Question,
    [property: JsonPropertyName("header"), Description("Short title for this question.")] string Header,
    [property: JsonPropertyName("options"), Description("Suggested answers; do not add a custom-answer option, UI provides it automatically.")] AskOption[] Options,
    [property: JsonPropertyName("multiple"), Description("Allow selecting multiple answers.")] bool Multiple = false);

internal sealed class AskTool(Func<AskQuestion[], CancellationToken, Task<string[][]>>? ask)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(AskAsync, name: "ask_tool");

    private readonly SemaphoreSlim _pending = new(1, 1);

    [Description("Ask the user questions in an interactive panel replacing chat input and wait for their answers. Always use this tool for questions, never ask in chat. Supports suggested options, multiple selection and automatic custom text answers. Dismissal stops the current request.")]
    public async Task<string> AskAsync(
        [Description("One to eight concise questions to ask together.")] AskQuestion[] questions,
        CancellationToken cancellationToken = default)
    {
        if (questions is null || questions.Length is < 1 or > 8)
            return "Error: Provide one to eight questions.";
        foreach (var question in questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Question) || question.Question.Length > 2000 ||
                string.IsNullOrWhiteSpace(question.Header) || question.Header.Length > 80 ||
                question.Options is null || question.Options.Length > 12 ||
                question.Options.Any(option => option is null || string.IsNullOrWhiteSpace(option.Label) ||
                    option.Label.Length > 200 || option.Description is null || option.Description.Length > 1000) ||
                question.Options.Select(option => option.Label).Distinct(StringComparer.Ordinal).Count() != question.Options.Length)
                return "Error: Questions require a title (up to 80 characters), text (up to 2000), and up to 12 distinct options with labels (up to 200) and descriptions (up to 1000).";
        }
        if (ask is null) return "Error: Interactive questions are unavailable. Do not ask in chat instead.";
        await _pending.WaitAsync(cancellationToken);
        try
        {
            var answers = await ask(questions, cancellationToken);
            return JsonSerializer.Serialize(new { questions = questions.Select(question => question.Question), answers });
        }
        finally { _pending.Release(); }
    }
}
