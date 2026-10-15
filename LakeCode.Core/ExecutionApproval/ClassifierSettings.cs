using LakeCode.Core.Providers;

namespace LakeCode.Core.ExecutionApproval;

/// <summary>Role assignment only; credentials belong exclusively to ProviderProfile.</summary>
public sealed record ClassifierModelSelection(string ProviderId, string ModelId)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(ProviderId, "D", out _) || !ClassificationModel.IsVersionedId(ModelId))
            throw new InvalidDataException("Выберите подключение и фиксированный ID jev-X.Y.Z для классификатора в /models.");
    }

    public ProviderProfile Resolve(ProviderRegistry providers)
    {
        Validate();
        var provider = providers.Profiles.FirstOrDefault(p => p.Id == ProviderId)
            ?? throw new InvalidDataException("Подключение классификатора недоступно. Настройте /providers и /models.");
        if (provider.Kind != ProviderKind.TypeSafe || !provider.ClassificationModels.Any(m => m.Id == ModelId && m.IsVersioned))
            throw new InvalidDataException("Модель классификатора недоступна или несовместима. Настройте /providers и /models.");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidDataException("Укажите ключ классификатора через /providers.");
        provider.Validate();
        return provider;
    }
}

public sealed record ClassifierSettings
{
    public bool Enabled { get; init; }
    public ClassifierModelSelection? Model { get; init; }
    public int TimeoutSeconds { get; init; } = 20;
    public double AllowThreshold { get; init; } = 0.8;
    public double DenyThreshold { get; init; } = 0.95;

    public void Validate()
    {
        Model?.Validate();
        if ((Enabled && Model is null) || TimeoutSeconds is < 1 or > 120 ||
            !double.IsFinite(AllowThreshold) || AllowThreshold is < 0.8 or > 1 ||
            !double.IsFinite(DenyThreshold) || DenyThreshold is < 0.9 or > 1)
            throw new InvalidDataException("Настройте классификатор в /models: модель, timeout 1–120, порог авторазрешения 0.8–1 и запрета 0.9–1.");
    }

    internal ClassifierConfiguration Capture(ProviderRegistry providers)
    {
        Validate();
        if (!Enabled) return new(this, null, null);
        try { return new(this, Model!.Resolve(providers), null); }
        catch (InvalidDataException error) { return new(this, null, error.Message); }
    }
}

/// <summary>Runtime-only snapshot. Missing configuration is visible and never replaced with another model.</summary>
internal sealed record ClassifierConfiguration(ClassifierSettings Settings, ProviderProfile? Provider, string? Error)
{
    public override string ToString() => $"Classifier ({Settings.Model?.ModelId}, enabled: {Settings.Enabled})";
}
