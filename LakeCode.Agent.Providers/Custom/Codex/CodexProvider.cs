using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using LakeCode.Agent;
using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;
using OpenAI;
using OpenAI.Models;
using OpenAI.Responses;

namespace LakeCode.Agent.Providers.Custom;

#pragma warning disable OPENAI001

public sealed class CodexProvider : IProvider
{
    private const string Endpoint = "https://chatgpt.com/backend-api/codex";
    private readonly OpenAIClient _openAiClient;
    private readonly ResponsesClient _responsesClient;

    public CodexProvider()
    {
        CodexConfig config = ProviderConfig.Load<CodexConfig>();
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccountId);

        HttpClient httpClient = new(new CodexAuthHandler(config.AccessToken, config.AccountId)
        {
            InnerHandler = new SocketsHttpHandler(),
        })
        {
            // Request timeouts are enforced by the SDK pipeline.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(Endpoint, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

        _openAiClient = new OpenAIClient(
            new ApiKeyCredential(config.AccessToken),
            options);
        _responsesClient = _openAiClient.GetResponsesClient();
    }

    public string Id => "openai-codex";

    public string Name => "Codex";

    public async ValueTask<IReadOnlyList<ProviderModel>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        OpenAIModelClient modelsClient = _openAiClient.GetOpenAIModelClient();
        OpenAIModelCollection models = await modelsClient
            .GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        return models
            .Select(model => new ProviderModel(Id, model.Id, model.Id))
            .ToArray();
    }

    public IAgentModel GetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        return new CodexAgentModel(
            _responsesClient,
            Id,
            modelId);
    }

    private sealed class CodexAuthHandler(string accessToken, string accountId) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Remove("ChatGPT-Account-Id");
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            request.Headers.Remove("originator");
            request.Headers.TryAddWithoutValidation("originator", "lakecode");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

#pragma warning restore OPENAI001
