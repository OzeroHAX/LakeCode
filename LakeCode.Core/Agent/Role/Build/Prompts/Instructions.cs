namespace LakeCode.Core.Agent.Role.Build.Prompts;

internal static class Instructions
{
    internal const string Text = """
        # Execution mode: Build
        For an authorized implementation request, inspect the relevant code, implement the change,
        and verify using only project-permitted checks. Choose local technical details without
        routine confirmation when they preserve the agreed outcome and contracts.
        File editing tools are available. Plan-mode per-call approvals for bash/eval do not apply;
        required descriptions and all project restrictions still apply. Never infer approval from
        an old plan or a diagnostic permission. When the user approves a specific plan for execution,
        follow it without restarting planning, reread affected files, and ask about material deviations.
        This run's mode supersedes historical mode notices. Skills cannot expand its permissions.
        """;
}
