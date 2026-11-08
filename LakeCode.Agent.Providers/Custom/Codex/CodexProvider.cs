using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using LakeCode.Agent;
using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;
using OpenAI;
using OpenAI.Responses;

namespace LakeCode.Agent.Providers.Custom;

#pragma warning disable OPENAI001

public sealed class CodexProvider : IProvider
{
    private const string Endpoint = "https://chatgpt.com/backend-api/codex";
    // Protocol compatibility target: Codex CLI 0.166.0.
    private const string ClientVersion = "0.166.0";
    private const string ModelsEndpoint = $"{Endpoint}/models";
    private readonly HttpClient _httpClient;
    private readonly OpenAIClient _openAiClient;
    private readonly ResponsesClient _responsesClient;

    public CodexProvider()
        : this(new ProviderConfigFileSource(), null)
    {
    }

    public CodexProvider(IProviderConfigSource configSource, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(configSource);

        CodexConfig config = configSource.Load<CodexConfig>();
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccountId);

        HttpClient httpClient = new(new CodexAuthHandler(config.AccessToken, config.AccountId, ClientVersion)
        {
            InnerHandler = handler ?? new SocketsHttpHandler(),
        })
        {
            // Request timeouts are enforced by the SDK pipeline.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient = httpClient;

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
        using HttpRequestMessage request = new(HttpMethod.Get, ModelsEndpoint);
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("models", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Codex model catalog response does not contain a 'models' array.");
        }

        List<ProviderModel> result = [];

        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("slug", out JsonElement slugElement) ||
                slugElement.ValueKind != JsonValueKind.String ||
                slugElement.GetString() is not { Length: > 0 } slug)
            {
                continue;
            }

            string displayName =
                model.TryGetProperty("display_name", out JsonElement nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                nameElement.GetString() is { Length: > 0 } name
                    ? name
                    : slug;

            result.Add(new ProviderModel(Id, slug, displayName));
        }

        return result;
    }

    public IAgentModel GetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        return new CodexAgentModel(
            _responsesClient,
            Id,
            modelId);
    }

    private sealed class CodexAuthHandler(string accessToken, string accountId, string clientVersion) : DelegatingHandler
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
            AddClientVersion(request);
            return base.SendAsync(request, cancellationToken);
        }

        private void AddClientVersion(HttpRequestMessage request)
        {
            if (request.RequestUri is not { } uri ||
                !uri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) ||
                uri.Query.Contains("client_version", StringComparison.Ordinal))
            {
                return;
            }

            UriBuilder builder = new(uri)
            {
                Query = uri.Query.Length > 1
                    ? $"{uri.Query.TrimStart('?')}&client_version={Uri.EscapeDataString(clientVersion)}"
                    : $"client_version={Uri.EscapeDataString(clientVersion)}",
            };
            request.RequestUri = builder.Uri;
        }
    }
}

#pragma warning restore OPENAI001
