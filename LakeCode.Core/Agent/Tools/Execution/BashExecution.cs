namespace LakeCode.Core.Agent;

/// <summary>Bounded display output, separate from the tool result sent to the model.</summary>
public sealed record BashExecution(
    DateTimeOffset StartedAt,
    DateTimeOffset? LastOutputAt,
    string Output,
    bool Truncated,
    double ElapsedSeconds,
    int? ExitCode);

public sealed record BashOutputUpdate(string CallId, BashExecution Execution);
