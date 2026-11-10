using System.Text.Json;

namespace LakeCode.Agent;

public abstract record AgentMessage(string MessageId, AgentMessageRole Role);

public sealed record UserMessage(string MessageId, string Text)
    : AgentMessage(MessageId, AgentMessageRole.User);

public sealed record SystemMessage(string MessageId, string Text)
    : AgentMessage(MessageId, AgentMessageRole.System);

public sealed record AssistantMessage(
    string MessageId,
    string Text,
    IReadOnlyList<AgentToolCall> ToolCalls)
    : AgentMessage(MessageId, AgentMessageRole.Assistant);

public sealed record ToolResultMessage(
    string MessageId,
    string ToolCallId,
    string ToolName,
    bool IsError,
    string Result,
    IReadOnlyList<AgentImage>? Images = null)
    : AgentMessage(MessageId, AgentMessageRole.ToolResult);

public sealed record AgentToolCall(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments);
