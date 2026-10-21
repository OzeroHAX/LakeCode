using LakeCode.Core.Agent.Delegation;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Subagents.Tools;

internal sealed class WaitSubagentsTool(DelegationCoordinator coordinator)
{
    public AIFunction AgentTool() => AIFunctionFactory.Create(coordinator.WaitAsync, name: "wait_subagents");
}
