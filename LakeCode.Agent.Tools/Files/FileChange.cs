namespace LakeCode.Agent.Tools.Files;

/// <summary>Contents of a completed write, for preview and diffing by the host. Null Before means no preview.</summary>
public sealed record FileChange(string Path, string? Before, string After, string? PreviewError = null);
