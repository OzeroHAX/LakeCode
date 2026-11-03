namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    /// <summary>Path resolution and access rules common to every role with file tools.</summary>
    internal const string Paths = """
        Their supplied schemas define the accepted arguments. File tools accept workspace-relative
        paths and do not follow symbolic links. read_file, list_directory, search_text,
        find_files, outline_file and trace_code also accept
        external absolute paths: the application asks the user for read-only access to the
        containing directory and its descendants, remembering allow or deny for this session.
        Invoke the appropriate read tool to request access; respect denial and do not retry to
        bypass it. write_file and edit_file must stay within the configured workspace root.
        Bash starts in a workspace directory but is not an OS sandbox: commands have the
        application's permissions. Access and execution restrictions apply regardless of the
        mechanism: do not bypass them through bash, eval, direct filesystem or network APIs,
        child processes, or indirect calls.
        """;
}
