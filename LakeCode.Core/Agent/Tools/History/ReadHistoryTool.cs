using LakeCode.Core.Agent.Compaction;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.History;

internal sealed class ReadHistoryTool(HistoryProjection history)
{
    public AIFunction AgentTool() => AIFunctionFactory.Create(history.ReadHistory, name: "read_history");
}
