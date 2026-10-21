using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Tools.Registration;

namespace LakeCode.Core.Agent.Execution;

/// <summary>Execution configuration shared by conversation roles and independent subagents.</summary>
internal sealed class AgentConfiguration(string instructions, WorkspaceFiles files)
{
    public string Instructions { get; } = instructions;
    public WorkspaceFiles Files { get; } = files;
    public AgentTools Tools { get; } = new();
    public bool UseSkills { get; init; }
    public bool? AllowMultipleToolCalls { get; init; }
    public string? PermissionCallId { get; init; }
    public Func<bool> IsComplete { get; init; } = () => false;
    public string CompletionError { get; init; } = "Error: Turn is complete.";
}
