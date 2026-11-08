using LakeCode.Agent;

namespace LakeCode.Agent.Providers.Base;

public enum ProviderModelType
{
    Unknown,
    Chat,
    ImageGeneration,
    Decision,
}

public enum ProviderModelModality
{
    Text,
    Image,
}

public sealed record ProviderModel(
    string ProviderId,
    string Id,
    string Name)
{
    public int? ContextWindowTokens { get; init; }

    public int? MaxInputTokens { get; init; }

    public int? MaxOutputTokens { get; init; }

    public IReadOnlyList<ProviderModelModality> InputModalities { get; init; } = [];

    public IReadOnlyList<ProviderModelModality> OutputModalities { get; init; } = [];

    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];

    public ProviderModelType Type { get; init; } = ProviderModelType.Unknown;
}

public interface IProvider
{
    string Id { get; }

    string Name { get; }

    ValueTask<IReadOnlyList<ProviderModel>> GetModelsAsync(
        CancellationToken cancellationToken = default);

    IAgentModel GetModel(string modelId);
}

