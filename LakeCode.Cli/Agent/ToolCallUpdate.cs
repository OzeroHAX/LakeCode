namespace LakeCode.Cli.Agent;

public enum ToolCallStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>A snapshot of an actual local function invocation, identified across start and finish.</summary>
public sealed record ToolCallUpdate(
    string CallId, string Name, string Arguments, ToolCallStatus Status, string? Result = null);
