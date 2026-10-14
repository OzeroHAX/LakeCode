namespace LakeCode.Core.Agent.Delegation;

public enum DelegationStatus
{
    Running,
    WaitingForParent,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Presentation snapshot, not the child's conversation or provider configuration.</summary>
public sealed record DelegationSnapshot(string TaskId, string Task, DelegationStatus Status,
    string? QuestionId = null, string? Question = null, string? Reason = null,
    string? Answer = null, string? Result = null, string? Error = null);

internal sealed record ParentQuestion(string Id, string Question, string Reason);
