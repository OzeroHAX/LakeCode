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

/// <summary>
/// Image produced by a tool. The bytes are already normalized for model input; the host decides which
/// models may receive them and never re-encodes them.
/// </summary>
public sealed record AgentImage(
    string MediaType,
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    string? SourcePath = null);

/// <summary>
/// Outcome of one tool call. Text travels separately from images, so a tool result never inlines base64
/// and the model sees the picture as image content.
/// </summary>
public sealed record AgentToolResult(
    bool IsError,
    string Result,
    IReadOnlyList<AgentImage>? Images = null);

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
