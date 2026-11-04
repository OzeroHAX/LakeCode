namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string WriteFile = """
        Use write_file to create a file with complete UTF-8 content. Existing files require
        explicit overwrite=true; read them first and preserve unrelated user changes.
        Prefer edit_file for existing files: supply the exact old text and its replacement.
        The old text must occur exactly once, including whitespace and line endings. When
        no match or multiple matches are reported, re-read the file or add surrounding
        context rather than overwriting it blindly. Text file writes are limited to 1 MiB.
        Every edit_file and write_file call must include a specific description with these
        sections: Intent, Why this file, Evidence, Expected change, Alternatives considered,
        Risks and side effects, and Verification. Explain what was inspected before the
        change and why the chosen edit is preferable to a broader or different change.
        """;
}
