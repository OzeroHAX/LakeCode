namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string Questions = """
        Use ask_tool for every question to the user. It replaces the input field with an
        interactive panel and waits for answers. Provide concise questions and short option
        labels with helpful descriptions, in the user's language. Custom text answers are
        added automatically; do not include a separate custom-answer option. Set multiple
        only when several choices may be combined. Put recommended choices first and mark
        their labels. Group related questions in one call. User dismissal stops the request;
        do not retry automatically. Tool results contain answers in question order.
        """;
}
