using System.Collections.Immutable;

namespace LakeCode.Core.Providers;

/// <summary>Ready connection derived from models.dev; the user supplies only a key.</summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    ProviderKind Kind,
    OpenAIApiMode ApiMode,
    string BaseUrl,
    string? DiscoveryUrl,
    string CatalogProviderId,
    ImmutableArray<string> EnvironmentVariables,
    string? DocumentationUrl,
    int ModelCount)
{
    public ProviderProfile CreateProfile(string name, string apiKey) => new()
    {
        Name = name,
        Kind = Kind,
        ApiMode = ApiMode,
        BaseUrl = BaseUrl,
        DiscoveryUrl = DiscoveryUrl,
        ApiKey = apiKey,
        CatalogProviderId = CatalogProviderId
    };
}

/// <summary>
/// Connections that the bundled OpenAI/Anthropic transports can use directly.
/// Provider names, endpoints and model capabilities stay in the generated models.dev catalog.
/// </summary>
public static class ProviderPresets
{
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

    public static ImmutableArray<ProviderPreset> All { get; } = Build();

    public static string? DiscoveryUrlFor(string? catalogProviderId) => catalogProviderId is not null
        ? All.FirstOrDefault(preset => preset.CatalogProviderId == catalogProviderId)?.DiscoveryUrl
        : null;

    private static ImmutableArray<ProviderPreset> Build()
    {
        var presets = ImmutableArray.CreateBuilder<ProviderPreset>();
        foreach (var provider in ModelsDevCatalog.Providers.Values)
        {
            if (provider.Models.Count == 0 || !TryMap(provider, out var kind, out var mode, out var baseUrl, out var discoveryUrl)) continue;
            presets.Add(new ProviderPreset(provider.Id, provider.Name, kind, mode, baseUrl, discoveryUrl, provider.Id,
                provider.EnvironmentVariables, provider.DocumentationUrl, provider.Models.Count));
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

    private static bool TryMap(ModelsDevProvider provider, out ProviderKind kind, out OpenAIApiMode mode,
        out string baseUrl, out string? discoveryUrl)
    {
        kind = ProviderKind.OpenAI;
        mode = OpenAIApiMode.ChatCompletions;
        discoveryUrl = null;
        switch (provider.Package)
        {
            case "@ai-sdk/openai-compatible":
                break;
            case "@ai-sdk/openai":
                if (provider.Id == "openai") mode = OpenAIApiMode.Responses;
                break;
            case "@ai-sdk/anthropic":
                kind = ProviderKind.Anthropic;
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
        if (!TryNormalize(raw, kind, out baseUrl)) return false;
        discoveryUrl = provider.Id switch
        {
            "opencode-go" => "https://opencode.ai/inference/go/v1/models",
            "opencode" => "https://opencode.ai/inference/v1/models",
            _ => null
        };
        return true;
    }

    private static bool TryNormalize(string? value, ProviderKind kind, out string baseUrl)
    {
        baseUrl = "";
        if (string.IsNullOrWhiteSpace(value) || value.Contains('{') || value.Contains('}') || value.Contains('$') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var normalized = value.Trim().TrimEnd('/');
        // The Anthropic SDK and discovery append /v1 themselves.
        if (kind == ProviderKind.Anthropic && normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3].TrimEnd('/');
        if (normalized.Length == 0) return false;
        baseUrl = normalized;
        return true;
    }
}
