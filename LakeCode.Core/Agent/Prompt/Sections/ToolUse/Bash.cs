namespace LakeCode.Core.Agent.Prompt.Sections;

internal static partial class ToolUse
{
    internal const string Bash = """
        Use bash for non-interactive commands, builds, and verification allowed by the user
        and project instructions. Supply a workspace-relative workingDirectory when needed.
        The tool returns the exit code, stdout, and stderr only after completion. There are
        no background jobs, polling, or interactive input. Do not launch background processes,
        servers, watchers, or programs that require terminal input. Output can be truncated.
        """;
}
