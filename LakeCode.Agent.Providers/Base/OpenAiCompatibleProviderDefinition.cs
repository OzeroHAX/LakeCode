using System.ClientModel;
using LakeCode.Agent;
using LakeCode.Agent.Providers.OpenAI;
using OpenAI;
using OpenAI.Models;

namespace LakeCode.Agent.Providers.Base;

public abstract class OpenAiCompatibleProvider : IProvider
{
    private readonly OpenAIClient _client;

    public abstract string Id { get; }

    public abstract string Name { get; }

    protected OpenAiCompatibleProvider(string endpoint, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _client = new(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint, UriKind.Absolute),
            });
    }

    public async ValueTask<IReadOnlyList<ProviderModel>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        OpenAIModelClient modelsClient = _client.GetOpenAIModelClient();
        OpenAIModelCollection models = await modelsClient.GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        return models
            .Select(model => new ProviderModel(
                Id,
                model.Id,
                model.Id))
            .ToArray();
    }

    public IAgentModel GetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        return new OpenAiAgentModel(
            _client.GetChatClient(modelId),
            Id,
            modelId);
    }

}
