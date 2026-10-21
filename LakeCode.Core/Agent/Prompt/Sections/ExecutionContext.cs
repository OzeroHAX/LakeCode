namespace LakeCode.Core.Agent.Prompt.Sections;

internal static class ExecutionContext
{
    public static string ForWorkspace(SystemPromptContext context) => $"""
        # Execution context
        Workspace root: {context.WorkspaceRoot}
        Operating system: {context.OSDescription}
        OS architecture: {context.OSArchitecture}
        Process architecture: {context.ProcessArchitecture}

        Use this platform information to tailor explanations and suggested commands.
        It does not grant permission or the ability to execute those commands.
        """;

    public static string ForSession(SystemPromptContext context) => $"""
        # Execution context
        Session temporary folder: {context.SessionWorkspaceRoot}
        Operating system: {context.OSDescription}
        OS architecture: {context.OSArchitecture}
        Process architecture: {context.ProcessArchitecture}

        Use this platform information to tailor explanations. It does not grant access to the
        user's project or permission to execute commands.
        """;
}
