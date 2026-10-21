using LakeCode.Core.Providers;

namespace LakeCode.Core.Settings;

/// <summary>User-owned image assignment. It never inherits the main chat model.</summary>
public sealed record ImageGenerationSelection(string ProviderId, string CarrierModelId, string ImageModelId)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(ProviderId, "D", out _) || !ProviderProfile.ValidModelId(CarrierModelId) ||
            !ProviderProfile.ValidModelId(ImageModelId))
            throw new InvalidDataException("Некорректное назначение генерации изображений.");
    }

    public ImageGenerationConfiguration Resolve(ProviderRegistry providers)
    {
        Validate();
        var provider = providers.Profiles.FirstOrDefault(p => p.Id == ProviderId)
            ?? throw new InvalidDataException("Подключение изображений недоступно. Измените назначение в /models.");
        if (provider.Kind != ProviderKind.OpenAI || provider.ApiMode != OpenAIApiMode.Responses)
            throw new InvalidDataException("Генерация изображений требует подключения OpenAI Responses.");
        if (ModelResolver.SelectionError(provider, CarrierModelId) is { } error)
            throw new InvalidDataException("Модель-посредник изображений: " + error);
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidDataException("Укажите ключ подключения изображений через /providers.");
        return new(this, provider, null);
    }
}

/// <summary>Runtime-only snapshot, including an unavailable state; never serialize credentials.</summary>
public sealed record ImageGenerationConfiguration(ImageGenerationSelection? Selection, ProviderProfile? Provider, string? Error)
{
    public static ImageGenerationConfiguration Capture(ImageGenerationSelection? selection, ProviderRegistry providers)
    {
        if (selection is null) return new(null, null, "Генерация изображений не настроена. Выберите модели в /models → Изображения.");
        try { return selection.Resolve(providers); }
        catch (InvalidDataException error) { return new(selection, null, error.Message); }
    }
}
