using System.Text.Json;

namespace LakeCode.Agent.Tools;

public sealed record AskOption(string Label, string Description);

public sealed record AskQuestion(
    string Question,
    string Header,
    AskOption[] Options,
    bool Multiple = false);

/// <summary>
/// Asks the user a question. The host supplies the interactive surface; the tool validates the payload
/// and serializes the answers so the model sees a stable shape.
/// </summary>
public sealed class AskTool(Func<AskQuestion[], CancellationToken, Task<string[][]>>? ask) : AgentTool
{
    private readonly SemaphoreSlim _pending = new(1, 1);

    public override string Name => "ask_tool";

    public override string Description =>
        "Ask the user questions in an interactive panel replacing chat input and wait for their answers. " +
        "Always use this tool for questions, never ask in chat. Dismissal stops the current request.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.PromptsUser);

    public override JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .ObjectArray(
            "questions",
            "One to eight concise questions to ask together.",
            question => question
                .Text("question", "Concise question in the user's language.")
                .Text("header", "Short title for this question.")
                .Boolean("multiple", "Allow selecting multiple answers.", false)
                .ObjectArray(
                    "options",
                    "Suggested answers; do not add a custom-answer option, the UI provides it automatically.",
                    option => option
                        .Text("label", "Short option label; put the recommended option first.")
                        .Text("description", "Brief explanation of the option.")),
            maxItems: 8,
            minItems: 1));

    protected override async ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGet("questions", out var questionsElement))
        {
            return Failure("Provide one to eight questions.");
        }

        List<AskQuestion> questions;

        try
        {
            questions = ReadQuestions(questionsElement);
        }
        catch (ToolArgumentException error)
        {
            return Failure(error.Message);
        }

        if (ask is null)
        {
            return Failure("Interactive questions are unavailable. Do not ask in chat instead.");
        }

        await _pending.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var answers = await ask([.. questions], cancellationToken).ConfigureAwait(false);

            return Success(JsonSerializer.Serialize(new
            {
                questions = questions.Select(question => question.Question),
                answers,
            }));
        }
        finally
        {
            _pending.Release();
        }
    }

    private static List<AskQuestion> ReadQuestions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException("'questions' must be an array.");
        }

        var questions = new List<AskQuestion>();

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ToolArgumentException("Each question must be an object.");
            }

            var text = ReadString(item, "question");
            var header = ReadString(item, "header");
            var multiple = item.TryGetProperty("multiple", out var multipleElement) &&
                           multipleElement.ValueKind == JsonValueKind.True;

            if (!item.TryGetProperty("options", out var optionsElement) ||
                optionsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ToolArgumentException("'options' must be an array.");
            }

            var options = new List<AskOption>();

            foreach (var option in optionsElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object)
                {
                    throw new ToolArgumentException("Each option must be an object.");
                }

                options.Add(new AskOption(ReadString(option, "label"), ReadString(option, "description")));
            }

            questions.Add(new AskQuestion(text, header, [.. options], multiple));
        }

        if (questions.Count is < 1 or > 8)
        {
            throw new ToolArgumentException("Provide one to eight questions.");
        }

        if (questions.Any(question => !Valid(question)))
        {
            throw new ToolArgumentException(
                "Questions require a title (up to 80 characters), text (up to 2000), and up to 12 distinct " +
                "options with labels (up to 200) and descriptions (up to 1000).");
        }

        return questions;
    }

    private static bool Valid(AskQuestion question) =>
        !string.IsNullOrWhiteSpace(question.Question) && question.Question.Length <= 2000 &&
        !string.IsNullOrWhiteSpace(question.Header) && question.Header.Length <= 80 &&
        question.Options.Length <= 12 &&
        question.Options.All(option => !string.IsNullOrWhiteSpace(option.Label) &&
                                       option.Label.Length <= 200 &&
                                       option.Description is not null &&
                                       option.Description.Length <= 1000) &&
        question.Options.Select(option => option.Label).Distinct(StringComparer.Ordinal).Count() ==
        question.Options.Length;

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"'{name}' is required and must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }
}
