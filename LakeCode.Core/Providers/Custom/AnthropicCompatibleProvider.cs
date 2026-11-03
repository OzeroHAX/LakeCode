using System.Collections.Immutable;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Providers.Custom;

/// <summary>Shared Anthropic Messages transport for API-key providers.</summary>
public abstract record AnthropicCompatibleProvider(
    string Id, string Name, string BaseUrl,
    string? DiscoveryUrl, string CatalogProviderId, ImmutableArray<string> EnvironmentVariables,
    string? DocumentationUrl, int ModelCount)
    : ApiKeyProvider(Id, Name, BaseUrl, DiscoveryUrl, CatalogProviderId, EnvironmentVariables, DocumentationUrl, ModelCount)
{
    public override string ProtocolLabel => "Anthropic · Messages";
    public override ModelReasoningFormat ReasoningFormat => ModelReasoningFormat.Budget;
    public override bool SupportsChat => true;
    internal override string ModelsUrl => DiscoveryUrl ?? BaseUrl.TrimEnd('/') + "/v1/models?limit=1000";
    internal override string? NextModelCursor(JsonElement root) =>
        root.TryGetProperty("has_more", out var more) && more.GetBoolean()
            ? root.GetProperty("last_id").GetString() ?? throw new InvalidDataException("Models API вернул некорректную пагинацию.") : null;
    internal override bool ValidEffort(string value, int maxOutput) => value == "none" ||
        (value is not null && value.StartsWith("budget:", StringComparison.Ordinal) &&
         int.TryParse(value[7..], out var budget) && budget >= 1024 && budget < maxOutput);
    internal override void Authorize(ProviderProfile profile, HttpRequestMessage request)
    {
        if (!HasCredentials(profile)) throw new InvalidOperationException("Не задан API-ключ. Настройте подключение через /providers.");
        request.Headers.Add("x-api-key", ApiKey(profile));
        request.Headers.Add("anthropic-version", "2023-06-01");
    }

    internal override IChatClient CreateChatClient(ProviderProfile profile, ModelInfo model, string sessionId)
    {
        profile.Validate();
        if (!HasCredentials(profile)) throw new InvalidOperationException("Не задан API-ключ. Настройте подключение через /providers.");
        var options = new ClientOptions { ApiKey = ApiKey(profile), BaseUrl = BaseUrl.TrimEnd('/') };
        return new AnthropicClient(options).AsIChatClient(model.Id, model.MaxOutputTokens);
    }

    internal override ChatOptions Options(ProviderProfile profile, ModelInfo model, string effort, string sessionId, bool title = false)
    {
        var options = new ChatOptions { ModelId = model.Id, MaxOutputTokens = title ? 1024 : model.MaxOutputTokens };
        options.RawRepresentationFactory = _ => new MessageCreateParams
        {
            Model = model.Id, MaxTokens = options.MaxOutputTokens!.Value, Messages = [],
            Thinking = !title && effort.StartsWith("budget:", StringComparison.Ordinal)
                ? new ThinkingConfigParam(new ThinkingConfigEnabled(budgetTokens: int.Parse(effort[7..]))) : null
        };
        return options;
    }
}
