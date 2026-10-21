using LakeCode.Core.Agent.Delegation;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Subagents.Tools;

internal sealed class SendSubagentMessageTool(DelegationCoordinator coordinator)
{
    public AIFunction AgentTool() => AIFunctionFactory.Create(coordinator.SendMessageAsync, name: "send_subagent_message");
}
