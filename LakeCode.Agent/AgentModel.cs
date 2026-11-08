namespace LakeCode.Agent;

public interface IAgentModel
{
    ValueTask<AgentModelResponse> RespondAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken);
}

public interface IStreamingAgentModel
{
    IAsyncEnumerable<AgentModelUpdate> RespondStreamingAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken = default);
}

public abstract record AgentModelUpdate;

public sealed record AgentTextDelta(string Text) : AgentModelUpdate;

public sealed record AgentThinkingDelta(string Thinking) : AgentModelUpdate;

public sealed record AgentToolCallStarted(
    string ToolCallId,
    string ToolName) : AgentModelUpdate;

public sealed record AgentToolCallDelta(
    string ToolCallId,
    string ArgumentsDelta) : AgentModelUpdate;

public sealed record AgentToolCallCompleted(AgentToolCall ToolCall) : AgentModelUpdate;

public sealed record AgentModelResponse(
    string Text,
    IReadOnlyList<AgentToolCall> ToolCalls);
