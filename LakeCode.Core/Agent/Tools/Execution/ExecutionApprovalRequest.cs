namespace LakeCode.Core.Agent;

/// <summary>One exact invocation; approval is never reusable.</summary>
public sealed record ExecutionApprovalRequest(string CallId, string ToolName, string Description, string Arguments)
{
    public ExecutionApproval.ExecutionAssessment? Assessment { get; init; }
}

