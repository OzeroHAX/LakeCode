namespace LakeCode.Core.Agent.Prompt.Sections;

/// <summary>
/// Tool guidance split by topic so each role composes only what it can actually do.
/// The blocks are joined by <see cref="SystemPromptComposer"/>; order here is prompt order.
/// </summary>
internal static partial class ToolUse
{
    /// <summary>Build: every tool in the catalog.</summary>
    internal static string Text => SystemPromptComposer.Compose(
        Header,
        Execution,
        EvalBridge,
        Questions,
        Skills,
        Paths,
        ListDirectory,
        Retrieval,
        ReadFile,
        Web,
        WriteFile,
        Images,
        Bash,
        OutputLimits);

    /// <summary>Plan: diagnostics and research only; no write_file, edit_file or image generation.</summary>
    internal static string ForPlan => SystemPromptComposer.Compose(
        Header,
        Execution,
        EvalBridge,
        Questions,
        Skills,
        Paths,
        ListDirectory,
        Retrieval,
        ReadFile,
        Web,
        Bash,
        OutputLimits);

    /// <summary>Read-only subagent: no commands, no questions, no web.</summary>
    internal static string ForSubagent => SystemPromptComposer.Compose(
        Header,
        PathsReadOnly,
        ListDirectory,
        Retrieval,
        ReadFile,
        OutputLimits);

    private const string PathsReadOnly = """
        Their supplied schemas define the accepted arguments. File tools accept workspace-relative
        paths and do not follow symbolic links. read_file, list_directory, search_text,
        find_files, outline_file and trace_code also accept
        external absolute paths: the application asks the user for read-only access to the
        containing directory and its descendants, remembering allow or deny for this session.
        Invoke the appropriate read tool to request access; respect denial and do not retry to
        bypass it. Access and execution restrictions apply regardless of the mechanism: do not
        bypass them through indirect calls.
        """;
}
