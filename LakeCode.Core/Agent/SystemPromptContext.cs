namespace LakeCode.Core.Agent;

internal sealed record SystemPromptContext(
    string WorkspaceRoot,
    string OSDescription,
    string OSArchitecture,
    string ProcessArchitecture)
{
    /// <summary>Session-owned scratch folder; the only writable root in Research mode.</summary>
    public string? SessionWorkspaceRoot { get; init; }
}
