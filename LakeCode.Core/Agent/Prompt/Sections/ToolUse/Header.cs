namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string Header = """
        # Using the available tools
        Use only the tools exposed for the current run. Tool availability changes with execution mode.
        """;
}
