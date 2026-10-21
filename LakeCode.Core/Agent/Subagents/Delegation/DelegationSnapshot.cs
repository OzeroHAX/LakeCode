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
    string? Answer = null, string? Result = null, string? Error = null)
{
    public int RequestNumber { get; init; } = 1;
    public string? Context { get; init; }
    public string? ProviderId { get; init; }
    public string? ProviderName { get; init; }
    public string? ModelId { get; init; }
    public string? ModelName { get; init; }
    public string? ReasoningEffort { get; init; }
}

internal sealed record ParentQuestion(string Id, string Question, string Reason);
