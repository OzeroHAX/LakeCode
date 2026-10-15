namespace LakeCode.Core.Agent;

public enum ToolCallStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    AwaitingApproval
}

/// <summary>A snapshot of an actual local function invocation, identified across start and finish.</summary>
public sealed record ToolCallUpdate(
    string CallId, string Name, string Arguments, ToolCallStatus Status, string? Result = null)
{
    public FileChange? FileChange { get; init; }
    public BashExecution? BashExecution { get; init; }
    public Delegation.DelegationSnapshot? Delegation { get; init; }
}

/// <summary>Contents associated with a successful file write; null Before means preview was unavailable.</summary>
public sealed record FileChange(string Path, string? Before, string After, string? PreviewError = null);
