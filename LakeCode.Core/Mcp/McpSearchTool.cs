using Microsoft.Extensions.AI;

namespace LakeCode.Core.Mcp;

internal sealed class McpSearchTool(McpSession session)
{
    public AIFunction AgentTool() => session.SearchTool();
}
