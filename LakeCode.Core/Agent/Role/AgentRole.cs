using LakeCode.Core.Agent.Execution;

namespace LakeCode.Core.Agent.Role;

internal abstract class AgentRole(AgentMode mode, AgentConfiguration configuration)
{
    public AgentMode Mode { get; } = mode;
    public AgentConfiguration Configuration { get; } = configuration;

    public virtual void BeginTurn(AgentMode? previousMode) { }
}
