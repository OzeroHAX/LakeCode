using LakeCode.Core.Agent.Delegation;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Subagents.Tools;

internal sealed class DelegateTool(DelegationCoordinator coordinator)
{
    public AIFunction AgentTool() => AIFunctionFactory.Create(coordinator.DelegateAsync, name: "delegate");
}
