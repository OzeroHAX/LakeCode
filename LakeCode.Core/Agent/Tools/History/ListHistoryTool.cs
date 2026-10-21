using LakeCode.Core.Agent.Compaction;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.History;

internal sealed class ListHistoryTool(HistoryProjection history)
{
    public AIFunction AgentTool() => AIFunctionFactory.Create(history.ListHistory, name: "list_history");
}
