using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.Registration;

/// <summary>One effective catalog per configuration, refreshed when context providers supply tools.</summary>
internal sealed class AgentTools
{
    private readonly Dictionary<string, ToolRegistration> _registered = new(StringComparer.Ordinal);
    private Dictionary<string, ToolRegistration> _effective = new(StringComparer.Ordinal);

    public ToolRegistration AddTool(AIFunction function)
    {
        var registration = new ToolRegistration(function);
        _registered.Add(function.Name, registration);
        return registration;
    }

    public IList<AITool> Functions => _registered.Values.Select(item => (AITool)item.Function).ToList();

    public void ResetEffective() => Volatile.Write(ref _effective, new(_registered, StringComparer.Ordinal));

    public void SetEffective(IEnumerable<AITool>? tools)
    {
        var effective = new Dictionary<string, ToolRegistration>(StringComparer.Ordinal);
        foreach (var function in (tools ?? []).OfType<AIFunction>())
        {
            // Context providers may contribute functions, but cannot replace a configured function's policy.
            var registration = _registered.TryGetValue(function.Name, out var configured)
                ? ReferenceEquals(configured.Function, function) ? configured
                    : throw new InvalidOperationException($"Context replaced registered tool '{function.Name}'.")
                : new ToolRegistration(function);
            effective.Add(function.Name, registration);
        }
        Volatile.Write(ref _effective, effective);
    }

    public bool TryGet(string name, out ToolRegistration registration) =>
        Volatile.Read(ref _effective).TryGetValue(name, out registration!);

    public string DescribeEvalTools() => JsonSerializer.Serialize(
        Volatile.Read(ref _effective).Values.Where(item => item.IsEvalAllowed).Select(item => new
        {
            item.Function.Name,
            item.Function.Description,
            Parameters = item.Function.JsonSchema
        }));
}
