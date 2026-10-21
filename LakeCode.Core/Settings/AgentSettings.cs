using System.Text.Json;
using System.Text.Json.Serialization;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;

namespace LakeCode.Core.Settings;

/// <summary>Serializable model assignment. Null assignment means inherit the main request.</summary>
public sealed record ModelSelection(string ProviderId, string ModelId, string ReasoningEffort)
{
    public void Validate(string role = "сабагентов")
    {
        if (!Guid.TryParseExact(ProviderId, "D", out _) || string.IsNullOrWhiteSpace(ModelId) ||
            string.IsNullOrWhiteSpace(ReasoningEffort))
            throw new InvalidDataException($"Некорректное назначение модели {role}.");
    }

    public ModelConfiguration Resolve(ProviderRegistry providers, string role = "сабагентов")
    {
        Validate(role);
        var provider = providers.Profiles.FirstOrDefault(item => item.Id == ProviderId)
            ?? throw new InvalidDataException($"Подключение {role} недоступно. Измените назначение в /models или восстановите подключение через /providers.");
        if (ModelResolver.SelectionError(provider, ModelId) is { } selectionError)
            throw new InvalidDataException($"Модель {role}: {selectionError}");
        var model = ModelResolver.Resolve(provider, ModelId).Model!;
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidDataException($"Укажите API-ключ подключения {role} через /providers.");
        if (!model.ReasoningEfforts.Contains(ReasoningEffort))
            throw new InvalidDataException($"Reasoning назначенной модели {role} больше не поддерживается. Измените назначение в /models.");
        return new ModelConfiguration(model, ReasoningEffort, provider);
    }
}

/// <summary>Runtime-only immutable connection snapshot; never persist credentials.</summary>
public sealed record ModelConfiguration(ModelInfo Model, string ReasoningEffort, ProviderProfile Provider);

public sealed record AgentSettings
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    public ModelSelection? SubagentModel { get; init; }
    public ModelSelection? PlanModel { get; init; }
    // Null means not configured: /compact never silently sends history to another model.
    public ModelSelection? CompactionModel { get; init; }
    // Null means disabled, never inherit a chat or subagent assignment.
    public ImageGenerationSelection? ImageGeneration { get; init; }
    public ExecutionApproval.ClassifierSettings Classifier { get; init; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая версия settings.json.");
        SubagentModel?.Validate();
        PlanModel?.Validate("планирования");
        CompactionModel?.Validate("сжатия контекста");
        ImageGeneration?.Validate();
        if (Classifier is null) throw new InvalidDataException("Некорректные настройки классификатора.");
        Classifier.Validate();
    }
}
