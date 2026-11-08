namespace LakeCode.Agent;

public enum AgentStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Canceled,
    Stopped,
}

/// <summary>Operational snapshot of one session, readable without watching the event stream.</summary>
public sealed record AgentState(
    long Revision,
    AgentStatus Status,
    bool IsBusy,
    string? RunId,
    string? Error,
    int MessageCount);

/// <summary>Outcome of a run. The terminal status is also published as an event.</summary>
public sealed record AgentRunResult(
    AgentStatus Status,
    string Text,
    string? Error,
    int Turns);

/// <summary>Session limits. A <see cref="MaxSteps"/> of 0 or less means no limit.</summary>
public sealed record AgentSessionOptions(int MaxSteps = 0)
{
    public static AgentSessionOptions Default { get; } = new();
}
