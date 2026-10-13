namespace LakeCode.Core.Agent;

internal sealed record SystemPromptContext(
    string WorkspaceRoot,
    string OSDescription,
    string OSArchitecture,
    string ProcessArchitecture);
