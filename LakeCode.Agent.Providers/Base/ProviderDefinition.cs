using LakeCode.Agent;

namespace LakeCode.Agent.Providers.Base;

public interface IProvider
{
    string Id { get; }

    string Name { get; }

    IAgentModel GetModel(string modelId);
}

public abstract class Provider : IProvider
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract IAgentModel GetModel(string modelId);
}
