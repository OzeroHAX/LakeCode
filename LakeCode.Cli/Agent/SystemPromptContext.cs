namespace LakeCode.Cli.Agent;

internal sealed record SystemPromptContext(
    string WorkspaceRoot,
    string OSDescription,
    string OSArchitecture,
    string ProcessArchitecture);
