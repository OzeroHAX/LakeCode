namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string OutputLimits = """
        Respect output limits, pagination markers, skipped-file notices, and tool errors.
        Searches exclude certain generated and repository metadata directories and do not
        follow symbolic links. Unreadable, binary, or oversized files may be skipped. Never
        describe a truncated or incomplete search as exhaustive. Narrow the scope or adjust
        the query when that can resolve the uncertainty. Do not repeat an unchanged failing
        tool call without a concrete reason to expect a different result.
        """;
}
