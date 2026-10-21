namespace LakeCode.Core.Agent;

public enum AgentMode { Build, Plan, Research }

/// <summary>One exact invocation; approval is never reusable.</summary>
public sealed record ExecutionApprovalRequest(string CallId, string ToolName, string Description, string Arguments)
{
    public ExecutionApproval.ExecutionAssessment? Assessment { get; init; }
}

/// <summary>Immutable, session-owned plan revision. Never a workspace file.</summary>
public sealed record ImplementationPlan(Guid Id, int Revision, string Markdown);