namespace LakeCode.Cli.Agent;

public enum ExternalReadDecision
{
    DenyOnce,
    DenySession,
    AllowSession
}

/// <summary>Read-only access to a directory and its descendants for one agent session.</summary>
public sealed record ExternalReadRequest(string CallId, string ToolName, string Path, string Directory);
