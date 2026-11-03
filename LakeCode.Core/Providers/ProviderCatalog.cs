using System.Collections.Immutable;
using LakeCode.Core.Providers.Custom;

namespace LakeCode.Core.Providers;

/// <summary>
/// Connections that the bundled OpenAI/Anthropic transports can use directly.
/// Provider names, endpoints and model capabilities stay in the generated models.dev catalog.
/// </summary>
public static class ProviderCatalog
{
    private static readonly Dictionary<string, Func<ModelsDevProvider, string, string?, Provider>> Overrides =
        new(StringComparer.Ordinal)
        {
            ["openai"] = static (catalog, endpoint, modelsEndpoint) => new OpenAIProvider(catalog, endpoint, modelsEndpoint),
            ["opencode"] = static (catalog, endpoint, modelsEndpoint) => new OpenCodeProvider(catalog, endpoint, modelsEndpoint),
            ["opencode-go"] = static (catalog, endpoint, modelsEndpoint) => new OpenCodeProvider(catalog, endpoint, modelsEndpoint),
            ["openrouter"] = static (catalog, endpoint, modelsEndpoint) => new OpenRouterProvider(catalog, endpoint, modelsEndpoint)
        };

    private static readonly ImmutableDictionary<string, string> FallbackBaseUrls =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openai"] = "https://api.openai.com/v1",
            ["anthropic"] = "https://api.anthropic.com",
            ["google"] = "https://generativelanguage.googleapis.com/v1beta/openai",
            ["groq"] = "https://api.groq.com/openai/v1",
            ["mistral"] = "https://api.mistral.ai/v1",
            ["xai"] = "https://api.x.ai/v1",
            ["cerebras"] = "https://api.cerebras.ai/v1",
            ["togetherai"] = "https://api.together.xyz/v1",
            ["deepinfra"] = "https://api.deepinfra.com/v1/openai"
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableArray<string> Preferred =
        ["openai", "anthropic", "google", "deepseek", "openrouter", "opencode-go", "opencode", "groq", "mistral", "xai"];

    public static ImmutableArray<Provider> All { get; } = Build();

    public static Provider Get(string id) => All.FirstOrDefault(provider => provider.Id == id)
        ?? throw new InvalidDataException("Провайдер недоступен. Проверьте настройки подключения.");

    private static ImmutableArray<Provider> Build()
    {
        var presets = ImmutableArray.CreateBuilder<Provider>();
        presets.Add(new TypeSafeProvider());
        presets.Add(new NeuralDeepProvider());
        presets.Add(new WikiVibeProvider());
        foreach (var provider in ModelsDevCatalog.Providers.Values)
        {
            if (provider.Id is "neuraldeep" or "wikivibe") continue;
            if (provider.Models.Count == 0 || !TryMap(provider, out var baseUrl)) continue;
            presets.Add(Overrides.TryGetValue(provider.Id, out var create)
                ? create(provider, baseUrl, null)
                : ModelsDevCatalog.CreateProvider(provider, baseUrl, null));
        }
        return presets
            .OrderBy(preset => Rank(preset.Id))
            .ThenBy(preset => preset.Name, StringComparer.Ordinal)
            .ThenBy(preset => preset.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static int Rank(string id)
    {
        var index = Preferred.IndexOf(id);
        return index < 0 ? int.MaxValue : index;
    }

    private static bool TryMap(ModelsDevProvider provider, out string baseUrl)
    {
        var anthropic = false;
        switch (provider.Package)
        {
            case "@ai-sdk/openai-compatible":
                break;
            case "@ai-sdk/openai":
                break;
            case "@ai-sdk/anthropic":
                anthropic = true;
                break;
            case "@openrouter/ai-sdk-provider":
            case "@ai-sdk/google":
            case "@ai-sdk/groq":
            case "@ai-sdk/mistral":
            case "@ai-sdk/xai":
            case "@ai-sdk/cerebras":
            case "@ai-sdk/togetherai":
            case "@ai-sdk/deepinfra":
                break;
            default:
                baseUrl = "";
                return false;
        }

        var raw = string.IsNullOrWhiteSpace(provider.BaseUrl)
            ? FallbackBaseUrls.GetValueOrDefault(provider.Id)
            : provider.BaseUrl;
        if (!TryNormalize(raw, anthropic, out baseUrl)) return false;
        return true;
    }

    private static bool TryNormalize(string? value, bool anthropic, out string baseUrl)
    {
        baseUrl = "";
        if (string.IsNullOrWhiteSpace(value) || value.Contains('{') || value.Contains('}') || value.Contains('$') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var normalized = value.Trim().TrimEnd('/');
        // The Anthropic SDK and discovery append /v1 themselves.
        if (anthropic && normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3].TrimEnd('/');
        if (normalized.Length == 0) return false;
        baseUrl = normalized;
        return true;
    }
}
