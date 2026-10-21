namespace LakeCode.Core.Agent.Delegation;

/// <summary>Live presentation event; not part of the parent conversation or provider input.</summary>
public sealed record DelegationActivity(string TaskId, string Kind, string? Text = null,
    ToolCallUpdate? ToolCall = null);
