using System.Collections.Immutable;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Providers;

public static class ProviderModel
{
    public const int MaxModels = 10000;
    public static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 512 && !id.Any(char.IsControl);
}

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
    /// <summary>The explicit ID is a decision model, not a chat model.</summary>
    public bool Decision { get; init; }
}

public sealed record DiscoveredModels(ImmutableArray<string> Ids, DateTimeOffset UpdatedAt)
{
    public ImmutableArray<string> DecisionIds { get; init; } = [];
    public ImmutableDictionary<string, ModelCapabilities> Capabilities { get; init; } = ImmutableDictionary<string, ModelCapabilities>.Empty;
}

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
        if (!profile.Definition.SupportsChat)
            return new(apiId, null, "Модель решений", false, "Этот провайдер не предоставляет чат-модели.");

        profile.ModelOverrides.TryGetValue(apiId, out var rule);
        if (rule?.Decision == true || profile.Discovery?.DecisionIds.Contains(apiId, StringComparer.Ordinal) == true)
            return new(apiId, null, "Модель решений", false, "Модель решений нельзя использовать в чате.") { Type = ModelType.Decision };
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
                fromCatalog = profile.Definition.FindModel(apiId) is not null;
                model = definition.Apply(apiId);
                source = "Полное описание";
            }
            else
            {
                model = rule?.CatalogModelId is null
                    ? profile.Definition.FindModel(apiId)
                    : null;
                if (model is not null) source = "Встроенная модель провайдера";
                else model = ModelsDevCatalog.ResolveModel(profile.Definition.CatalogProviderId, rule?.CatalogModelId, apiId,
                    profile.Definition.ReasoningFormat, out source);
                if (model is not null)
                {
                    // Catalog mappings supply capabilities, but requests must retain the provider's API ID.
                    model = model with { Id = apiId };
                    fromCatalog = true;
                    if (profile.Discovery?.Capabilities.TryGetValue(apiId, out var discovered) == true)
                    {
                        if (discovered.ContextWindowTokens is { } context)
                            model = model with { MaxInputTokens = Math.Min(model.MaxInputTokens, context), MaxOutputTokens = Math.Min(model.MaxOutputTokens, context) };
                        model = discovered.Apply(apiId, model);
                        source += " + данные подключения";
                    }
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
                    if (profile.Definition.RequiresKnownCapabilities(profile))
                        throw new InvalidDataException("Характеристики модели неизвестны. Задайте их вручную через /providers.");
                    model = ModelsDevCatalog.CreateGenericModel(apiId);
                    source = "Общие параметры";
                    if (rule?.Overrides is not null)
                    {
                        model = rule.Overrides.Apply(apiId, model);
                        source += " + overrides";
                    }
                }
            }

            if (model is not null) profile.Definition.ValidateModel(model);
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
        var available = (listing is ModelListingState.Manual or ModelListingState.Listed or ModelListingState.Catalog) &&
            profile.Definition.IsModelAllowed(profile, apiId);
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
        if (!ProviderModel.ValidId(apiId)) return "Введите корректный API ID модели.";
        var entry = Resolve(profile, apiId);
        if (entry.Type == ModelType.ImageGeneration)
            return "Это модель генерации изображений, не чат-модель и не carrier. Назначьте её как image model ID.";
        if (entry.Error is not null) return entry.Error;
        if (!profile.Definition.IsModelAllowed(profile, apiId)) return "Модель отсутствует в списке выбранного способа входа. Обновите список моделей.";
        return entry.Available ? null : ListingDescription(entry.Listing) + ". Обновите список или явно разрешите ручной ID в /models.";
    }

    public static ImmutableArray<ResolvedProviderModel> ResolveAll(ProviderProfile profile) =>
        profile.Definition.ModelIds(profile)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal)
        .Select(id => Resolve(profile, id))
        .ToImmutableArray();

}

