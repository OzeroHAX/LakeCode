namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string ListDirectory = """
        Use list_directory to discover immediate children of a directory. It is not a
        recursive tree listing. When the result provides a continuation offset, request the
        next page if those entries are needed. An incomplete page is not proof that a path
        does not exist. Entries identified as links do not imply that their targets can be
        opened by the other tools.
        """;
}
