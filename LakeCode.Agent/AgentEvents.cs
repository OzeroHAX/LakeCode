using System.Text.Json;

namespace LakeCode.Agent;

public abstract record AgentEvent
{
    public virtual bool IsDelta => false;
}

public sealed record AgentStarted(string RunId) : AgentEvent;
public sealed record AgentCompleted : AgentEvent;
public sealed record AgentFailed(string Error) : AgentEvent;
public sealed record AgentCanceled : AgentEvent;

public sealed record TurnStarted(string TurnId) : AgentEvent;
public sealed record TurnCompleted(string TurnId) : AgentEvent;

public sealed record MessageStarted(
    string MessageId,
    AgentMessageRole Role) : AgentEvent;

public sealed record MessageCompleted(
    string MessageId,
    AgentMessageRole Role) : AgentEvent;

public sealed record ModelResponseStarted(string ResponseId) : AgentEvent;
public sealed record ModelResponseCompleted(string ResponseId) : AgentEvent;
public sealed record ModelResponseFailed(
    string ResponseId,
    string Error) : AgentEvent;

public sealed record TextStarted(string MessageId) : AgentEvent;

public sealed record TextDelta(
    string MessageId,
    string Delta) : AgentEvent
{
    public override bool IsDelta => true;
}

public sealed record TextCompleted(
    string MessageId,
    string Text) : AgentEvent;

public sealed record ThinkingStarted(string MessageId) : AgentEvent;

public sealed record ThinkingDelta(
    string MessageId,
    string Delta) : AgentEvent
{
    public override bool IsDelta => true;
}

public sealed record ThinkingCompleted(
    string MessageId,
    string Thinking) : AgentEvent;

public sealed record ToolCallStarted(
    string MessageId,
    string ToolCallId,
    string ToolName) : AgentEvent;

public sealed record ToolCallDelta(
    string MessageId,
    string ToolCallId,
    string Delta) : AgentEvent
{
    public override bool IsDelta => true;
}

public sealed record ToolCallCompleted(
    string MessageId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments) : AgentEvent;

public sealed record ToolExecutionStarted(
    string ToolCallId,
    string ToolName) : AgentEvent;

public sealed record ToolExecutionDelta<T>(
    string CallId,
    T Data) : AgentEvent
{
    public override bool IsDelta => true;
}

public sealed record ToolExecutionCompleted<T>(
    string ToolCallId,
    bool IsError,
    T? Result) : AgentEvent;

public enum AgentMessageRole
{
    User,
    Assistant,
    ToolResult,
    System,
}
