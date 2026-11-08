using System.ClientModel;
using LakeCode.Agent;
using LakeCode.Agent.Providers.OpenAI;
using OpenAI;
using OpenAI.Chat;

namespace LakeCode.Agent.Providers.Base;

public abstract class OpenAiCompatibleProvider : Provider
{
    protected abstract string Endpoint { get; }

    protected abstract string ApiKey { get; }

    public override IAgentModel GetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var apiKey = ApiKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                $"Provider '{Id}' has an invalid inference endpoint.");
        }

        var client = new ChatClient(
            modelId,
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint.ToString().TrimEnd('/'))
            });

        return new OpenAiAgentModel(client);
    }
}
