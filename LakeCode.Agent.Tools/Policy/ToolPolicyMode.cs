namespace LakeCode.Agent.Tools.Policy;

/// <summary>What the current run is allowed to do. Chosen per run by the orchestration layer.</summary>
public sealed record ToolPolicyMode(
    bool AllowWrites,
    bool AllowProcess,
    bool AllowExternalService,
    bool AllowUserPrompts,
    bool AllowUncoveredTools,
    bool RequireApprovalForWrites,
    bool RequireApprovalForProcess)
{
    /// <summary>Inspect and answer questions; change nothing and start nothing.</summary>
    public static ToolPolicyMode ReadOnly { get; } = new(
        AllowWrites: false,
        AllowProcess: false,
        AllowExternalService: true,
        AllowUserPrompts: true,
        AllowUncoveredTools: false,
        RequireApprovalForWrites: false,
        RequireApprovalForProcess: false);

    /// <summary>Plan: the same restrictions as read-only.</summary>
    public static ToolPolicyMode Plan { get; } = ReadOnly;

    /// <summary>Interactive work with explicit approval for process execution.</summary>
    public static ToolPolicyMode Interactive { get; } = new(
        AllowWrites: true,
        AllowProcess: true,
        AllowExternalService: true,
        AllowUserPrompts: true,
        AllowUncoveredTools: false,
        RequireApprovalForWrites: false,
        RequireApprovalForProcess: true);

    /// <summary>Unattended run: no process execution, no external calls and nobody to ask.</summary>
    public static ToolPolicyMode Restricted { get; } = new(
        AllowWrites: true,
        AllowProcess: false,
        AllowExternalService: false,
        AllowUserPrompts: false,
        AllowUncoveredTools: false,
        RequireApprovalForWrites: false,
        RequireApprovalForProcess: false);
}
