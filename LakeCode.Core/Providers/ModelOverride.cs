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

public enum ModelListingState { NotLoaded, Listed, Catalog, Missing, Manual }

public sealed record ResolvedProviderModel(string ApiId, ModelInfo? Model, string Source, bool Available, string? Error)
{
    // Listing is independent of capability resolution and does not assert live API support.
    public ModelListingState Listing { get; init; }
    public ModelType Type { get; init; } = ModelType.Unknown;
}

public static class ModelResolver
{
    public static ResolvedProviderModel Resolve(ProviderProfile profile, string apiId)
    {
        if (profile.Kind == ProviderKind.TypeSafe)
            return new(apiId, null, "Классификация", false, "System One не является чат-моделью.");

        profile.ModelOverrides.TryGetValue(apiId, out var rule);
        var isImage = ModelCatalog.GetModelType(apiId) == ModelType.ImageGeneration;
        ModelInfo? model = null;
        var fromCatalog = false;
        string source;
        string? error = null;
        try
        {
            if (isImage)
            {
                source = "Генерация изображений";
            }
            else if (rule?.Definition is { } definition)
            {
                if (rule.CatalogModelId is not null || rule.Overrides is not null)
                    throw new InvalidDataException("Definition нельзя совмещать с CatalogModelId или Overrides.");
                model = definition.Apply(apiId);
                source = "Полное описание";
            }
            else
            {
                model = ModelsDevCatalog.ResolveModel(profile.CatalogProviderId, rule?.CatalogModelId, apiId,
                    profile.Kind, out source);
                if (model is not null)
                {
                    fromCatalog = true;
                    if (rule?.Overrides is not null)
                    {
                        model = rule.Overrides.Apply(apiId, model);
                        source += " + overrides";
                    }
                }
                else if (rule?.CatalogModelId is not null)
                {
                    throw new InvalidDataException("Указанная модель каталога не найдена.");
                }
                else
                {
                    model = ModelsDevCatalog.CreateGenericModel(apiId);
                    source = "Общие параметры";
                }
            }

            if (model is not null) profile.ValidateModel(model);
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            source = "Нужно настроить";
            model = null;
        }

        var listing = rule?.Manual == true ? ModelListingState.Manual
            : profile.Discovery is null ? (fromCatalog ? ModelListingState.Catalog : ModelListingState.NotLoaded)
            : profile.Discovery.Ids.Contains(apiId, StringComparer.Ordinal) ? ModelListingState.Listed
            : fromCatalog ? ModelListingState.Catalog
            : ModelListingState.Missing;
        var available = listing is ModelListingState.Manual or ModelListingState.Listed or ModelListingState.Catalog;
        return new(apiId, model, source, available, error)
        {
            Listing = listing,
            Type = isImage ? ModelType.ImageGeneration : model is null ? ModelType.Unknown : ModelType.Chat
        };
    }

    public static string ListingDescription(ModelListingState state) => state switch
    {
        ModelListingState.Manual => "Ручной ID; наличие на сервере не проверено",
        ModelListingState.Listed => "Объявлен в последнем списке провайдера",
        ModelListingState.Catalog => "Есть в каталоге models.dev; последний список провайдера её не подтверждает",
        ModelListingState.Missing => "ID отсутствует в последнем ответе сервера",
        _ => "Список провайдера ещё не получен"
    };

    public static string? SelectionError(ProviderProfile profile, string apiId)
    {
        if (!ProviderProfile.ValidModelId(apiId)) return "Введите корректный API ID модели.";
        var entry = Resolve(profile, apiId);
        if (entry.Type == ModelType.ImageGeneration)
            return "Это модель генерации изображений, не чат-модель и не carrier. Назначьте её как image model ID.";
        if (entry.Error is not null) return entry.Error;
        return entry.Available ? null : ListingDescription(entry.Listing) + ". Обновите список или явно разрешите ручной ID в /models.";
    }

    public static ImmutableArray<ResolvedProviderModel> ResolveAll(ProviderProfile profile) =>
        (profile.CatalogProviderId is { } catalogProviderId ? ModelsDevCatalog.ModelIdsForProvider(catalogProviderId) : [])
        .Concat(profile.Discovery?.Ids ?? [])
        .Concat(profile.ModelOverrides.Keys)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal)
        .Select(id => Resolve(profile, id))
        .ToImmutableArray();
}
