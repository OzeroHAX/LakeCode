using System.Text.Json;

namespace LakeCode.Agent.Tools;

/// <summary>
/// Capability-level tool: it performs one operation and reports its own outcome. Whether a call is
/// allowed, needs approval, or is forbidden in the current mode is decided by agent hooks, never here.
/// </summary>
public abstract class AgentTool : IAgentTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonElement? ParametersSchema { get; }

    /// <summary>Declared effects; hooks read this instead of recognizing tool names.</summary>
    public virtual AgentToolMetadata Metadata => AgentToolMetadata.Inert;

    public async ValueTask<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await ExecuteCoreAsync(new ToolArguments(arguments), context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolArgumentException error)
        {
            return Failure(error.Message);
        }
    }

    protected abstract ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken);

    /// <summary>Validates that every named rationale field is present and non-empty.</summary>
    protected static void RequireAll(ToolArguments arguments, params string[] names)
    {
        foreach (var name in names)
        {
            arguments.RequireString(name);
        }
    }

    protected static AgentToolResult Success(string result) => new(false, result);

    protected static AgentToolResult Failure(string error) => new(true, "Error: " + error);

    /// <summary>
    /// Converts expected I/O and validation failures into a model-visible error result. Unexpected
    /// exceptions keep propagating so the runner reports a real defect instead of a tool message.
    /// </summary>
    protected static async ValueTask<AgentToolResult> GuardAsync(Func<Task<AgentToolResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToolArgumentException error)
        {
            return Failure(error.Message);
        }
        catch (Exception error) when (IsExpected(error))
        {
            return Failure(error.Message);
        }
    }

    private static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
            or NotSupportedException or System.Text.RegularExpressions.RegexMatchTimeoutException;
}
