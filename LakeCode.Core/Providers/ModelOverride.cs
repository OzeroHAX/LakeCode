using System.Collections.Immutable;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Providers;

/// <summary>Nullable fields inherit from the catalog; arrays replace rather than merge.</summary>
public sealed record ModelCapabilities
{
    public string? Name { get; init; }
    public int? ContextWindowTokens { get; init; }
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public ImmutableArray<ModelModality>? InputModalities { get; init; }
    public ImmutableArray<ModelModality>? OutputModalities { get; init; }
    public ImmutableArray<string>? ReasoningEfforts { get; init; }

    public ModelInfo Apply(string apiId, ModelInfo? basis = null) => new(apiId,
        Name ?? basis?.Name ?? throw Missing("Name"),
        ContextWindowTokens ?? basis?.ContextWindowTokens ?? throw Missing("ContextWindowTokens"),
        MaxInputTokens ?? basis?.MaxInputTokens ?? throw Missing("MaxInputTokens"),
        MaxOutputTokens ?? basis?.MaxOutputTokens ?? throw Missing("MaxOutputTokens"),
        InputModalities ?? basis?.InputModalities ?? throw Missing("InputModalities"),
        OutputModalities ?? basis?.OutputModalities ?? throw Missing("OutputModalities"),
        ReasoningEfforts ?? basis?.ReasoningEfforts ?? throw Missing("ReasoningEfforts"));

    private static InvalidDataException Missing(string field) => new($"Полное описание модели требует {field}.");

    public static ModelCapabilities FromModel(ModelInfo model) => new()
    {
        Name = model.Name, ContextWindowTokens = model.ContextWindowTokens, MaxInputTokens = model.MaxInputTokens,
        MaxOutputTokens = model.MaxOutputTokens, InputModalities = model.InputModalities,
        OutputModalities = model.OutputModalities, ReasoningEfforts = model.ReasoningEfforts
    };
}

public sealed record ModelOverride
{
    public string? CatalogModelId { get; init; }
    public ModelCapabilities? Overrides { get; init; }
    public ModelCapabilities? Definition { get; init; }
    /// <summary>Explicitly allow an API ID without discovery (e.g. gateways without Models API).</summary>
    public bool Manual { get; init; }
}

public sealed record DiscoveredModels(ImmutableArray<string> Ids, DateTimeOffset UpdatedAt);

public sealed record ResolvedProviderModel(string ApiId, ModelInfo? Model, string Source, bool Available, string? Error);

public static class ModelResolver
{
    public static ResolvedProviderModel Resolve(ProviderProfile profile, string apiId)
    {
        if (profile.Kind == ProviderKind.TypeSafe)
            return new(apiId, null, "Классификация", false, "System One не является чат-моделью.");
        profile.ModelOverrides.TryGetValue(apiId, out var rule);
        var available = rule?.Manual == true || profile.Discovery?.Ids.Contains(apiId, StringComparer.Ordinal) == true;
        try
        {
            ModelInfo model;
            string source;
            if (rule?.Definition is { } definition)
            {
                if (rule.CatalogModelId is not null || rule.Overrides is not null)
                    throw new InvalidDataException("Definition нельзя совмещать с CatalogModelId или Overrides.");
                model = definition.Apply(apiId);
                source = "Полное описание";
            }
            else
            {
                var catalogId = rule?.CatalogModelId ?? apiId;
                if (!ModelCatalog.Models.TryGetValue(catalogId, out var basis))
                    throw new InvalidDataException(rule?.CatalogModelId is null
                        ? "Нужно настроить: модель отсутствует в каталоге."
                        : "Указанная модель каталога не найдена.");
                model = (rule?.Overrides ?? new ModelCapabilities()).Apply(apiId, basis);
                source = rule?.CatalogModelId is not null ? "Сопоставлена" : "Из каталога";
                if (rule?.Overrides is not null) source += " + overrides";
            }
            profile.ValidateModel(model);
            return new(apiId, model, source, available, null);
        }
        catch (InvalidDataException error) { return new(apiId, null, "Нужно настроить", available, error.Message); }
    }

    public static ImmutableArray<ResolvedProviderModel> ResolveAll(ProviderProfile profile) =>
        (profile.Discovery?.Ids ?? []).Concat(profile.ModelOverrides.Keys).Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal).Select(id => Resolve(profile, id)).ToImmutableArray();
}
