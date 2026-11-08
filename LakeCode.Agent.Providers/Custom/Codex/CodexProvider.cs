using LakeCode.Agent;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class CodexProvider : IProvider
{
    public string Id => "openai-codex";

    public string Name => "Codex";

    public ValueTask<IReadOnlyList<ProviderModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Codex model listing is not implemented yet.");

    public IAgentModel GetModel(string modelId) =>
        throw new NotSupportedException(
            "Codex model creation is not implemented yet.");
}
