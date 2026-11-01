using System.Collections.Frozen;
using System.Collections.Immutable;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Providers;

public static partial class ModelsDevCatalog
{
    private static readonly Lazy<FrozenDictionary<string, ImmutableArray<CatalogCandidate>>> CandidatesByModelId =
        new(BuildCandidateIndex);

    private static readonly Lazy<FrozenDictionary<string, string>> ProviderIdByBaseUrl = new(BuildBaseUrlIndex);

    private static readonly Lazy<FrozenDictionary<string, string>> ProviderIdByHost = new(BuildHostIndex);

    private static readonly Lazy<ModelInfo> DefaultChatModel = new(ResolveDefaultChatModel);

    public static ModelInfo DefaultModel => DefaultChatModel.Value;

    /// <summary>Infers the models.dev provider from an OpenAI/Anthropic-compatible base URL.</summary>
    public static string? InferProviderId(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (NormalizeUrl(baseUrl) is { } normalized && ProviderIdByBaseUrl.Value.TryGetValue(normalized, out var providerId))
            return providerId;
        return ProviderIdByHost.Value.TryGetValue(uri.Host.ToLowerInvariant(), out providerId) ? providerId : null;
    }

    /// <summary>Resolves a chat model from the models.dev catalog, preferring the profile's provider.</summary>
    public static ModelInfo? ResolveModel(string? catalogProviderId, string? catalogModelId, string apiId,
        ProviderKind kind, out string source)
    {
        if (catalogProviderId is not null && Providers.TryGetValue(catalogProviderId, out var provider))
        {
            if (provider.Models.TryGetValue(apiId, out var providerModel))
            {
                source = "Из каталога models.dev";
                return ToModelInfo(providerModel, kind);
            }
            if (catalogModelId is not null && provider.Models.TryGetValue(catalogModelId, out var mappedProviderModel))
            {
                source = "Сопоставлена с models.dev";
                return ToModelInfo(mappedProviderModel, kind);
            }
        }

        if (catalogModelId is not null)
        {
            var mapped = FindMappedModel(catalogModelId, kind);
            if (mapped is null)
            {
                source = "";
                return null;
            }
            source = "Сопоставлена с models.dev";
            return ToModelInfo(mapped.Model, kind);
        }

        var found = FindMappedModel(apiId, kind);
        if (found is null)
        {
            source = "";
            return null;
        }
        source = "Из каталога models.dev";
        return ToModelInfo(found.Model, kind);
    }

    public static ImmutableArray<string> ModelIdsForProvider(string providerId) =>
        Providers.TryGetValue(providerId, out var provider)
            ? provider.Models.Keys.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray()
            : [];

    public static ModelInfo CreateGenericModel(string apiId) => new(
        apiId, apiId, 131_072, 131_072, 32_768,
        [ModelModality.Text], [ModelModality.Text], ["none"]);

    public static ModelInfo ToModelInfo(ModelsDevModel model, ProviderKind kind) => new(
        model.Id,
        string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
        model.ContextWindowTokens,
        model.MaxInputTokens,
        model.MaxOutputTokens,
        ToInputModalities(model),
        [ModelModality.Text],
        ToReasoningEfforts(model, kind));

    private static ModelInfo ResolveDefaultChatModel()
    {
        if (Providers.TryGetValue("openai", out var openai) && openai.Models.TryGetValue("gpt-6.1-sol", out var preferred))
            return ToModelInfo(preferred, ProviderKind.OpenAI);
        var first = Providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .SelectMany(provider => provider.Models.Values.OrderBy(model => model.Id, StringComparer.Ordinal))
            .FirstOrDefault();
        return first is null
            ? CreateGenericModel("unknown")
            : ToModelInfo(first, ProviderKind.OpenAI);
    }

    private static ImmutableArray<ModelModality> ToInputModalities(ModelsDevModel model)
    {
        var values = ImmutableArray.CreateBuilder<ModelModality>();
        if (model.InputModalities.Contains("text", StringComparer.OrdinalIgnoreCase)) values.Add(ModelModality.Text);
        if (model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase)) values.Add(ModelModality.Image);
        if (values.Count == 0) values.Add(ModelModality.Text);
        return values.ToImmutable();
    }

    private static ImmutableArray<string> ToReasoningEfforts(ModelsDevModel model, ProviderKind kind)
    {
        if (!model.Reasoning) return ["none"];
        if (kind == ProviderKind.OpenAI)
        {
            var efforts = model.ReasoningOptions
                .Where(option => option.Type == "effort")
                .SelectMany(option => option.Values)
                .Where(ProviderProfile.SupportedEfforts.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            return efforts.IsEmpty ? ["none"] : efforts;
        }
        if (kind == ProviderKind.Anthropic)
        {
            var budgets = model.ReasoningOptions
                .Where(option => option.Type == "budget_tokens")
                .SelectMany(option => new[] { 1024, 4096, 8192, 16384, 32768 }
                    .Where(budget => budget >= Math.Max(1024, option.Min ?? 1024) && budget < model.MaxOutputTokens))
                .Distinct()
                .Order()
                .Select(budget => $"budget:{budget}");
            var values = new List<string> { "none" };
            values.AddRange(budgets);
            return values.ToImmutableArray();
        }
        return ["none"];
    }

    private static CatalogCandidate? FindMappedModel(string modelId, ProviderKind kind)
    {
        if (CandidatesByModelId.Value.TryGetValue(modelId, out var exact))
            return Select(exact, modelId, kind);
        var separator = modelId.LastIndexOf('/');
        if (separator >= 0 && separator < modelId.Length - 1 &&
            CandidatesByModelId.Value.TryGetValue(modelId[(separator + 1)..], out var suffix))
            return Select(suffix, modelId[(separator + 1)..], kind);
        return null;
    }

    private static CatalogCandidate Select(ImmutableArray<CatalogCandidate> candidates, string modelId, ProviderKind kind)
    {
        var priority = PreferredProviders;
        return candidates
            .OrderBy(candidate => KindRank(candidate.ProviderId, kind))
            .ThenBy(candidate => FamilyRank(candidate.ProviderId, modelId))
            .ThenBy(candidate => priority.TryGetValue(candidate.ProviderId, out var rank) ? rank : int.MaxValue)
            .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal)
            .First();
    }

    private static int KindRank(string providerId, ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAI when providerId == "openai" => 0,
        ProviderKind.OpenAI => 10,
        ProviderKind.Anthropic when providerId == "anthropic" => 0,
        ProviderKind.Anthropic => 10,
        _ => 0
    };

    private static int FamilyRank(string providerId, string modelId)
    {
        var family = modelId switch
        {
            _ when modelId.StartsWith("gpt-", StringComparison.Ordinal) ||
                   modelId.StartsWith("chatgpt-", StringComparison.Ordinal) => "openai",
            _ when modelId.StartsWith("claude-", StringComparison.Ordinal) => "anthropic",
            _ when modelId.StartsWith("deepseek-", StringComparison.Ordinal) => "deepseek",
            _ when modelId.StartsWith("gemini-", StringComparison.Ordinal) => "google",
            _ when modelId.StartsWith("glm-", StringComparison.Ordinal) => "zai",
            _ when modelId.StartsWith("qwen", StringComparison.Ordinal) => "alibaba",
            _ when modelId.StartsWith("kimi", StringComparison.Ordinal) => "moonshot",
            _ when modelId.StartsWith("grok", StringComparison.Ordinal) => "xai",
            _ when modelId.StartsWith("mistral", StringComparison.Ordinal) => "mistral",
            _ => null
        };
        return family is not null && providerId.Contains(family, StringComparison.Ordinal) ? 0 : 1;
    }

    private static FrozenDictionary<string, ImmutableArray<CatalogCandidate>> BuildCandidateIndex()
    {
        var values = new Dictionary<string, List<CatalogCandidate>>(StringComparer.Ordinal);
        foreach (var provider in Providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal))
        foreach (var model in provider.Models.Values)
        {
            if (!values.TryGetValue(model.Id, out var candidates))
                values[model.Id] = candidates = [];
            candidates.Add(new CatalogCandidate(provider.Id, model));
        }
        return values.ToFrozenDictionary(item => item.Key, item => item.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, string> BuildBaseUrlIndex()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in Providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal))
        {
            if (NormalizeUrl(provider.BaseUrl) is { } url) values.TryAdd(url, provider.Id);
        }
        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, string> BuildHostIndex()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in Providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal))
        {
            if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)) continue;
            values.TryAdd(uri.Host.ToLowerInvariant(), provider.Id);
        }
        values.TryAdd("api.openai.com", "openai");
        values.TryAdd("api.anthropic.com", "anthropic");
        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string? NormalizeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3].TrimEnd('/');
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}";
    }

    private static readonly FrozenDictionary<string, int> PreferredProviders = new[]
    {
        "openai", "anthropic", "deepseek", "opencode-go", "opencode", "openrouter",
        "google", "zai", "moonshot", "alibaba", "mistral", "xai", "groq"
    }.Select((id, index) => (id, index)).ToFrozenDictionary(item => item.id, item => item.index, StringComparer.Ordinal);

    private sealed record CatalogCandidate(string ProviderId, ModelsDevModel Model);
}
