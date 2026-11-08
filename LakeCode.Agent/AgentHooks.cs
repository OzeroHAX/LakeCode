namespace LakeCode.Agent;

/// <summary>Run-scoped information passed to every hook.</summary>
public sealed record AgentHookContext(string RunId, int Turn, AgentHistory History);

/// <summary>What the model request will look like. A hook may return a modified copy.</summary>
public sealed record AgentModelRequest(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools);

public enum AgentToolDecisionKind
{
    Allow,
    Deny,
    Stop,
}

/// <summary>Result of a pre-tool hook. Deny records an error result; Stop ends the run.</summary>
public sealed record AgentToolDecision(AgentToolDecisionKind Kind, string? Reason = null)
{
    public static AgentToolDecision Allow { get; } = new(AgentToolDecisionKind.Allow);

    public static AgentToolDecision Deny(string reason) => new(AgentToolDecisionKind.Deny, reason);

    public static AgentToolDecision Stop(string reason) => new(AgentToolDecisionKind.Stop, reason);
}

/// <summary>
/// Participation points in the run. Hooks observe and may steer the loop; a hook exception fails
/// the run instead of being treated as permission, so policy checks cannot silently pass.
/// Combine several concerns by composing delegates in the caller.
/// </summary>
public sealed class AgentHooks
{
    public static AgentHooks None { get; } = new();

    public Func<AgentHookContext, CancellationToken, ValueTask>? BeforeRun { get; init; }

    public Func<AgentHookContext, CancellationToken, ValueTask>? AfterRun { get; init; }

    /// <summary>May rewrite the messages or tool list sent to the model.</summary>
    public Func<AgentHookContext, AgentModelRequest, CancellationToken, ValueTask<AgentModelRequest>>? BeforeModelRequest { get; init; }

    /// <summary>Observes the complete response of one turn, streaming and non-streaming alike.</summary>
    public Func<AgentHookContext, AgentModelResponse, CancellationToken, ValueTask>? AfterModelResponse { get; init; }

    /// <summary>Policy and approval point. Runs before a tool executes; may deny or stop.</summary>
    public Func<AgentHookContext, AgentToolCall, CancellationToken, ValueTask<AgentToolDecision>>? BeforeToolExecution { get; init; }

    /// <summary>Observes a tool result and may replace it. Successful side effects are not undone.</summary>
    public Func<AgentHookContext, AgentToolCall, AgentToolResult, CancellationToken, ValueTask<AgentToolResult>>? AfterToolExecution { get; init; }

    /// <summary>Diagnostics for a run that ends with an error. Cannot suppress the failure.</summary>
    public Func<AgentHookContext, Exception, CancellationToken, ValueTask>? OnError { get; init; }
}
