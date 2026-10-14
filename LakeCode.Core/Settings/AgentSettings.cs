using System.Text.Json;
using System.Text.Json.Serialization;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;

namespace LakeCode.Core.Settings;

/// <summary>Serializable model assignment. Null assignment means inherit the main request.</summary>
public sealed record ModelSelection(string ProviderId, string ModelId, string ReasoningEffort)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(ProviderId, "D", out _) || string.IsNullOrWhiteSpace(ModelId) ||
            string.IsNullOrWhiteSpace(ReasoningEffort))
            throw new InvalidDataException("Некорректное назначение модели сабагентов.");
    }

    public ModelConfiguration Resolve(ProviderRegistry providers)
    {
        Validate();
        var provider = providers.Profiles.FirstOrDefault(item => item.Id == ProviderId)
            ?? throw new InvalidDataException("Подключение сабагентов недоступно. Измените назначение в /models или восстановите подключение через /providers.");
        var model = provider.Models.FirstOrDefault(item => item.Id == ModelId)
            ?? throw new InvalidDataException("Модель сабагентов недоступна. Измените назначение в /models.");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidDataException("Укажите API-ключ подключения сабагентов через /providers.");
        if (!model.ReasoningEfforts.Contains(ReasoningEffort))
            throw new InvalidDataException("Reasoning назначенной модели сабагентов больше не поддерживается. Измените назначение в /models.");
        return new ModelConfiguration(model, ReasoningEffort, provider);
    }
}

/// <summary>Runtime-only immutable connection snapshot; never persist credentials.</summary>
public sealed record ModelConfiguration(ModelInfo Model, string ReasoningEffort, ProviderProfile Provider);

public sealed record AgentSettings
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    public ModelSelection? SubagentModel { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая версия settings.json.");
        SubagentModel?.Validate();
    }
}
