using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.Registration;

/// <summary>One effective catalog per configuration, refreshed when context providers supply tools.</summary>
internal sealed class AgentTools
{
    private readonly Dictionary<string, ToolRegistration> _registered = new(StringComparer.Ordinal);
    private Dictionary<string, ToolRegistration> _effective = new(StringComparer.Ordinal);
    private Dictionary<string, ToolRegistration> _dynamic = new(StringComparer.Ordinal);

    // Called at model-request boundaries, never from notification callbacks.
    public void SetDynamic(IEnumerable<ToolRegistration> registrations)
    {
        var next = registrations.ToDictionary(r => r.Function.Name, StringComparer.Ordinal);
        if (next.Keys.Any(_registered.ContainsKey)) throw new InvalidOperationException("Dynamic tool conflicts with a configured tool.");
        Volatile.Write(ref _dynamic, next);
    }

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
            if (!_registered.TryGetValue(function.Name, out var registration) &&
                !Volatile.Read(ref _dynamic).TryGetValue(function.Name, out registration))
            {
                effective.Add(function.Name, new ToolRegistration(function));
                continue;
            }
            if (!Matches(registration.Function, function))
                throw new InvalidOperationException($"Context replaced registered tool '{function.Name}'.");
            effective.Add(function.Name, registration);
        }
        Volatile.Write(ref _effective, effective);
    }

    private static bool Matches(AIFunction registered, AIFunction effective) =>
        ReferenceEquals(registered, effective) ||
        effective is DelegatingAIFunction && registered.Name == effective.Name &&
        registered.Description == effective.Description &&
        registered.JsonSchema.GetRawText() == effective.JsonSchema.GetRawText();

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
