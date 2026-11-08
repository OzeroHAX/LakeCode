using System.Text.Json;

namespace LakeCode.Agent;

public interface IAgentTool
{
    string Name { get; }

    string Description => string.Empty;

    JsonElement? ParametersSchema => null;

    ValueTask<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolContext context,
        CancellationToken cancellationToken);
}

public sealed record AgentToolDefinition(
    string Name,
    string Description = "",
    JsonElement? ParametersSchema = null);

public sealed record AgentToolResult(
    bool IsError,
    string Result);

public sealed class AgentToolContext
{
    private readonly Action<AgentEvent> _publish;

    internal AgentToolContext(
        string callId,
        string toolName,
        Action<AgentEvent> publish)
    {
        CallId = callId;
        ToolName = toolName;
        _publish = publish;
    }

    public string CallId { get; }

    public string ToolName { get; }

    public void PublishProgress<T>(T data)
    {
        _publish(new ToolExecutionDelta<T>(CallId, data));
    }
}
