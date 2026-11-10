namespace LakeCode.Agent.Tools;

/// <summary>
/// What a capability can touch. This is a fact about the implementation, not a permission: hooks combine
/// effects with the current mode to decide whether a call is allowed, denied, or needs approval.
/// </summary>
[Flags]
public enum AgentToolEffects
{
    None = 0,
    ReadsWorkspace = 1 << 0,
    WritesWorkspace = 1 << 1,
    ExecutesProcess = 1 << 2,
    CallsExternalService = 1 << 3,
    PromptsUser = 1 << 4,
}

/// <summary>Declared effects of one tool, used by policy hooks instead of matching tool names.</summary>
public sealed record AgentToolMetadata(AgentToolEffects Effects)
{
    public static AgentToolMetadata Inert { get; } = new(AgentToolEffects.None);

    public bool ReadsWorkspace => (Effects & AgentToolEffects.ReadsWorkspace) != 0;

    public bool WritesWorkspace => (Effects & AgentToolEffects.WritesWorkspace) != 0;

    public bool ExecutesProcess => (Effects & AgentToolEffects.ExecutesProcess) != 0;

    public bool CallsExternalService => (Effects & AgentToolEffects.CallsExternalService) != 0;

    public bool PromptsUser => (Effects & AgentToolEffects.PromptsUser) != 0;
}
