namespace LakeCode.Agent.Tools;

/// <summary>
/// Resolves the metadata of tools by the name carried in <see cref="AgentToolCall"/>, so a hook does not
/// repeat a switch over tool names. Configuration errors surface at construction instead of mid-run.
/// </summary>
public sealed class AgentToolCatalog
{
    private readonly Dictionary<string, AgentTool> _tools;

    private AgentToolCatalog(Dictionary<string, AgentTool> tools) => _tools = tools;

    public IReadOnlyCollection<string> Names => _tools.Keys;

    public static AgentToolCatalog Create(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var map = new Dictionary<string, AgentTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);

            if (tool is not AgentTool described)
            {
                // Unknown effects cannot be assumed safe, so the catalog refuses to describe it.
                throw new InvalidOperationException(
                    $"Tool '{tool.Name}' does not derive from {nameof(AgentTool)} and has no declared effects.");
            }

            if (!map.TryAdd(tool.Name, described))
            {
                throw new InvalidOperationException($"Duplicate tool name '{tool.Name}'.");
            }
        }

        return new AgentToolCatalog(map);
    }

    public AgentToolMetadata Find(string toolName) =>
        _tools.TryGetValue(toolName, out var tool) ? tool.Metadata : AgentToolMetadata.Inert;

    public bool TryGet(string toolName, out AgentToolMetadata metadata)
    {
        if (_tools.TryGetValue(toolName, out var tool))
        {
            metadata = tool.Metadata;
            return true;
        }

        metadata = AgentToolMetadata.Inert;
        return false;
    }
}
