using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace LakeCode.Core.Providers;

#pragma warning disable OPENAI001, SCME0001
internal static class ProviderClientFactory
{
    private static readonly string UserAgentApplicationId =
        $"LakeCode/{typeof(ProviderClientFactory).Assembly.GetName().Version?.ToString(3) ?? "1.0"}";

    public static IChatClient Create(ProviderProfile profile, ModelInfo model, string sessionId)
    {
        profile.Validate();
        if (profile.Kind == ProviderKind.TypeSafe)
            throw new InvalidOperationException("System One поддерживает классификацию, не чат. Выберите чат-модель в /models.");
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            throw new InvalidOperationException("Не задан API-ключ. Настройте подключение через /providers.");
        var openCodeSession = UsesOpenCodeSession(profile);
        if (profile.Kind == ProviderKind.Anthropic)
        {
            var anthropicOptions = new ClientOptions { ApiKey = profile.ApiKey, BaseUrl = profile.BaseUrl.TrimEnd('/') };
            if (openCodeSession)
                anthropicOptions.ExtraHeaders = new Dictionary<string, string> { ["x-opencode-session"] = sessionId };
            return new AnthropicClient(anthropicOptions).AsIChatClient(model.Id, model.MaxOutputTokens);
        }
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(profile.BaseUrl.TrimEnd('/')), UserAgentApplicationId = UserAgentApplicationId
        };
        if (profile.ApiMode == OpenAIApiMode.Responses)
            options.AddPolicy(new CodexSessionHeadersPolicy(sessionId), PipelinePosition.PerCall);
        if (openCodeSession)
            options.AddPolicy(new OpenCodeSessionHeadersPolicy(sessionId), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(profile.ApiKey), options);
        return profile.ApiMode == OpenAIApiMode.Responses
            ? client.GetResponsesClient().AsIChatClient()
            : client.GetChatClient(model.Id).AsIChatClient();
    }

    private static bool UsesOpenCodeSession(ProviderProfile profile) =>
        profile.CatalogProviderId is "opencode" or "opencode-go" ||
        Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase);

    public static ChatOptions Options(ProviderProfile profile, ModelInfo model, string effort, string sessionId, bool title = false)
    {
        if (profile.Kind == ProviderKind.TypeSafe)
            throw new InvalidOperationException("System One не поддерживает чат-параметры.");
        var options = new ChatOptions { ModelId = model.Id, MaxOutputTokens = title ? 1024 : model.MaxOutputTokens };
        if (profile.Kind == ProviderKind.Anthropic)
        {
            options.RawRepresentationFactory = _ => new MessageCreateParams
            {
                Model = model.Id, MaxTokens = options.MaxOutputTokens!.Value, Messages = [],
                Thinking = !title && effort.StartsWith("budget:", StringComparison.Ordinal)
                    ? new ThinkingConfigParam(new ThinkingConfigEnabled(budgetTokens: int.Parse(effort[7..]))) : null
            };
        }
        else if (profile.ApiMode == OpenAIApiMode.ChatCompletions)
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
                    ReasoningOptions = new ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort)
                    }
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
}
#pragma warning restore OPENAI001, SCME0001
