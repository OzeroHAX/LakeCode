using System.Text.Json;
using LakeCode.Agent.Tools.Files;

namespace LakeCode.Agent.Tools.Policy;

/// <summary>
/// Composes host-owned path decisions with tool execution. This is argument extraction, not a sandbox:
/// commands, scripts and custom tools may access paths that are not explicit path arguments.
/// </summary>
public static class WorkspacePathHook
{
    public static Func<AgentHookContext, AgentToolCall, CancellationToken, ValueTask<AgentToolDecision>> Create(
        WorkspaceFiles files,
        Func<AgentToolCall, IReadOnlyList<string>, CancellationToken, ValueTask<AgentToolDecision>> decide,
        Func<AgentToolCall, IReadOnlyList<string>>? extract = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(decide);

        return (_, call, token) => decide(
            call,
            (extract is null ? Extract(call) : extract(call)).Select(files.Absolute).ToArray(),
            token);
    }

    /// <summary>
    /// Explicit path arguments used by the built-in tools. The host must handle defaults and additional
    /// arguments of custom tools through the extractor supplied to Create. Even an empty list reaches
    /// the decision callback, so the host can reject uncovered tools instead of silently allowing them.
    /// </summary>
    public static IReadOnlyList<string> Extract(AgentToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Arguments.ValueKind != JsonValueKind.Object) return [];

        var result = new List<string>();
        foreach (var name in new[] { "path", "workingDirectory", "output_path", "input_paths" })
        {
            if (!call.Arguments.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString()!);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) result.Add(item.GetString()!);
                }
            }
        }
        return result;
    }
}
