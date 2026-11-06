using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Immutable;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace LakeCode.Core.Providers.Custom;

public enum OpenAIApiMode { Responses, ChatCompletions }

/// <summary>Shared OpenAI-compatible chat transport for API-key providers.</summary>
public abstract record OpenAICompatibleProvider(
    string Id, string Name, OpenAIApiMode ApiMode, string BaseUrl,
    string? DiscoveryUrl, string CatalogProviderId, ImmutableArray<string> EnvironmentVariables,
    string? DocumentationUrl, int ModelCount)
    : ApiKeyProvider(Id, Name, BaseUrl, DiscoveryUrl, CatalogProviderId, EnvironmentVariables, DocumentationUrl, ModelCount)
{
    private static readonly string UserAgentApplicationId =
        $"LakeCode/{typeof(Provider).Assembly.GetName().Version?.ToString(3) ?? "1.0"}";
    public static readonly ImmutableArray<string> SupportedEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    public override string ProtocolLabel => $"OpenAI · {ApiMode}";
    public override ModelReasoningFormat ReasoningFormat => ModelReasoningFormat.Effort;
    public override bool SupportsChat => true;
    public override bool SupportsImageGeneration => ApiMode == OpenAIApiMode.Responses;
    internal override bool ValidEffort(string value, int maxOutput) => SupportedEfforts.Contains(value);
    internal virtual string GetEndpoint(ProviderProfile profile) => EndpointFor(AuthenticationMethod(profile));
    internal virtual string Credential(ProviderProfile profile) => ApiKey(profile);
    internal virtual void ConfigureOpenAI(OpenAIClientOptions options, ProviderProfile profile, string sessionId) { }

#pragma warning disable OPENAI001, SCME0001
    internal override IChatClient CreateChatClient(ProviderProfile profile, ModelInfo model, string sessionId,
        Action<ProviderResponseRouting>? responseReceived = null)
    {
        profile.Validate();
        if (!HasCredentials(profile)) throw new InvalidOperationException("Не задан API-ключ. Настройте подключение через /providers.");
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(GetEndpoint(profile).TrimEnd('/')), UserAgentApplicationId = UserAgentApplicationId
        };
        if (ApiMode == OpenAIApiMode.Responses)
            options.AddPolicy(new CodexSessionHeadersPolicy(sessionId), PipelinePosition.PerCall);
        if (CreateResponseHeadersPolicy(profile, model, responseReceived) is { } responsePolicy)
            options.AddPolicy(responsePolicy, PipelinePosition.PerCall);
        ConfigureOpenAI(options, profile, sessionId);
        var client = new OpenAIClient(new ApiKeyCredential(Credential(profile)), options);
        return ApiMode == OpenAIApiMode.Responses
            ? client.GetResponsesClient().AsIChatClient()
            : client.GetChatClient(model.Id).AsIChatClient();
    }

    internal virtual PipelinePolicy? CreateResponseHeadersPolicy(ProviderProfile profile, ModelInfo model,
        Action<ProviderResponseRouting>? responseReceived) => null;

    internal override ChatOptions Options(ProviderProfile profile, ModelInfo model, string effort, string sessionId, bool title = false)
    {
        var options = new ChatOptions { ModelId = model.Id, MaxOutputTokens = title ? 1024 : model.MaxOutputTokens };
        if (ApiMode == OpenAIApiMode.ChatCompletions)
        {
            options.RawRepresentationFactory = _ =>
            {
                var raw = new ChatCompletionOptions();
                if (effort != "none") raw.Patch.Set("$.reasoning_effort"u8, effort);
                return raw;
            };
        }
        else
        {
            options.RawRepresentationFactory = _ =>
            {
                var raw = new CreateResponseOptions
                {
                    StoredOutputEnabled = false,
                    ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort) }
                };
                if (!title && effort != "none")
                {
                    raw.ReasoningOptions.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
                    raw.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
                }
                raw.Patch.Set("$.prompt_cache_key"u8, sessionId);
                return raw;
            };
        }
        return options;
    }
#pragma warning restore OPENAI001, SCME0001
}
