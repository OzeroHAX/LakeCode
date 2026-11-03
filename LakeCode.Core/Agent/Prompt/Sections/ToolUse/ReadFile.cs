namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string ReadFile = """
        Use read_file to inspect UTF-8 source with line numbers. Request focused line ranges
        for large files, and read additional ranges when a declaration or control flow is
        incomplete. Distinguish end of file from a requested range ending. If output was cut
        off, retry with a narrower range; do not invent the unseen remainder of a line.
        """;
}
