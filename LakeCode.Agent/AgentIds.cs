namespace LakeCode.Agent;

internal static class AgentIds
{
    public static string Create() => Guid.NewGuid().ToString("N");
}
