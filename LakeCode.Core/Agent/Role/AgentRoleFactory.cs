using LakeCode.Core.Agent.Prompt;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Tools.Registration;

namespace LakeCode.Core.Agent.Role;

internal sealed class AgentRoleFactory
{
    private readonly Dictionary<AgentMode, AgentRole> _roles;

    public AgentRoleFactory(SystemPromptContext context, InstructionsReader instructions,
        WorkspaceFiles? researchFiles, AgentToolServices services, Func<string, ImplementationPlan?> savePlan)
    {
        _roles = new()
        {
            [AgentMode.Build] = new Build.BuildRole(context, instructions, services),
            [AgentMode.Plan] = new Plan.PlanRole(context, instructions, services, savePlan)
        };
        if (researchFiles is not null)
            _roles.Add(AgentMode.Research, new Research.ResearchRole(context, instructions, services.WithFiles(researchFiles)));
    }

    public AgentRole Get(AgentMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return _roles.TryGetValue(mode, out var role) ? role
            : throw new InvalidOperationException("Research mode requires a session temporary folder.");
    }
}
