using LakeCode.Core.Agent.Compaction;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.History;

internal sealed class SearchHistoryTool(HistoryProjection history)
{
    public AIFunction AgentTool() => AIFunctionFactory.Create(history.SearchHistory, name: "search_history");
}
