using LakeCode.Core.Agent.Execution;
using Microsoft.Agents.AI;

namespace LakeCode.Core.Agent.Skills;

/// <summary>Suppresses skills where unavailable and captures the effective functions for eval.</summary>
internal sealed class RoleAwareContextProvider(
    AIContextProvider? inner, Func<AgentConfiguration> getConfiguration, Action validateSkillRoots) : AIContextProvider
{
    // Preserve the inner provider's persisted identity across the refactor.
    public override IReadOnlyList<string> StateKeys => inner?.StateKeys ?? [];

    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        var configuration = getConfiguration();
        if (configuration.UseSkills) validateSkillRoots();
        // Own the merge; letting the base merge again would duplicate tools and instructions.
        var result = configuration.UseSkills && inner is not null
            ? await inner.InvokingAsync(context, cancellationToken) : context.AIContext;
        // MAF advertises run_skill_script even without a runner. Preserve the application's no-script contract.
        result.Tools = result.Tools?.Where(tool => tool.Name != "run_skill_script").ToArray();
        configuration.Tools.SetEffective(result.Tools);
        return result;
    }

    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken) =>
        inner?.InvokedAsync(context, cancellationToken) ?? ValueTask.CompletedTask;
}
