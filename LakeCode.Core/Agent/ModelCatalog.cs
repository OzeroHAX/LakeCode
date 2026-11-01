using System.Collections.Frozen;
using LakeCode.Core.Providers;

namespace LakeCode.Core.Agent;

public enum ModelType { Unknown, Chat, ImageGeneration }

public sealed record ImageModelInfo(string Id, string Name)
{
    public ModelType Type => ModelType.ImageGeneration;
}

/// <summary>
/// Image-generation aliases. Chat capabilities come from <see cref="ModelsDevCatalog"/>;
/// image IDs are kept explicit because hosted image generation accepts gateway-specific IDs
/// that are not part of the chat catalog.
/// </summary>
public static class ModelCatalog
{
    private static readonly ImageModelInfo[] ImageAliases =
    [
        new("gpt-image-1", "GPT Image 1"),
        new("gpt-image-1-mini", "GPT Image 1 Mini"),
        new("gpt-image-1.5", "GPT Image 1.5"),
        new("gpt-image-2", "GPT Image 2"),
        new("gpt-image-2.5", "GPT Image 2.5 (gateway alias)"),
        new("gpt-image-2.5-sunburst", "GPT Image 2.5 Sunburst"),
        new("gpt-image-2.5-flare", "GPT Image 2.5 Flare"),
        new("gpt-image-2-codex", "GPT Image 2 Codex (backend ID)")
    ];

    public static FrozenDictionary<string, ImageModelInfo> ImageModels { get; } =
        ImageAliases.ToFrozenDictionary(model => model.Id, StringComparer.Ordinal);

    public static ModelType GetModelType(string id) =>
        ImageModels.ContainsKey(id) ? ModelType.ImageGeneration : ModelType.Unknown;

    public static ModelInfo DefaultModel => ModelsDevCatalog.DefaultModel;
}
