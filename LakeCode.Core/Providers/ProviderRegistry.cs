using System.Collections.Immutable;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Providers;

/// <summary>Publishes immutable profiles only after durable persistence succeeds.</summary>
public sealed class ProviderRegistry
{
    private readonly ProviderStorage _storage = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private ImmutableArray<ProviderProfile> _profiles;
    public ImmutableArray<ProviderProfile> Profiles { get { lock (_gate) return _profiles; } }
    public ProviderRegistry() => _profiles = _storage.Load();

    public ProviderProfile Get(string id) => Profiles.FirstOrDefault(profile => profile.Id == id)
        ?? throw new InvalidOperationException("Провайдер недоступен. Выберите подключение через /providers.");

    public ModelInfo GetModel(string providerId, string modelId)
    {
        var profile = Get(providerId);
        if (ModelResolver.SelectionError(profile, modelId) is { } error)
            throw new InvalidOperationException(error);
        return ModelResolver.Resolve(profile, modelId).Model!;
    }

    public async Task SaveAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        profile = profile with { Revision = Guid.NewGuid().ToString("D") };
        profile.Validate();
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var current = Profiles;
            var index = Array.FindIndex(current.ToArray(), item => item.Id == profile.Id);
            var next = index < 0 ? current.Add(profile) : current.SetItem(index, profile);
            await _storage.SaveAsync(next, cancellationToken);
            lock (_gate) _profiles = next;
        }
        finally { _writes.Release(); }
    }

    public async Task RefreshDiscoveryAsync(string id, CancellationToken cancellationToken = default)
    {
        // Serialize refresh and profile writes, but never hold the publication lock across I/O.
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var profile = Get(id);
            var discovery = await ProviderModelDiscovery.DiscoverAsync(profile, cancellationToken);
            var updated = profile with { Discovery = discovery };
            await _storage.SaveDiscoveryAsync(updated, cancellationToken);
            lock (_gate)
            {
                var index = Array.FindIndex(_profiles.ToArray(), p => p.Id == id);
                _profiles = _profiles.SetItem(index, updated);
            }
        }
        finally { _writes.Release(); }
    }

    public async Task AllowManualModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
    {
        if (!ProviderProfile.ValidModelId(modelId)) throw new InvalidDataException("Введите корректный API ID модели.");
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var profile = Get(providerId);
            var entry = ModelResolver.Resolve(profile, modelId);
            if (entry.Model is null || entry.Error is not null)
                throw new InvalidDataException($"{entry.Error} Настройте характеристики чат-модели через /providers.");
            var rule = profile.ModelOverrides.GetValueOrDefault(modelId) ?? new ModelOverride();
            if (rule.Manual) return;
            var updated = profile with
            {
                Revision = Guid.NewGuid().ToString("D"),
                ModelOverrides = profile.ModelOverrides.SetItem(modelId, rule with { Manual = true })
            };
            updated.Validate();
            var current = Profiles;
            var index = Array.FindIndex(current.ToArray(), p => p.Id == providerId);
            var next = current.SetItem(index, updated);
            await _storage.SaveAsync(next, cancellationToken);
            lock (_gate) _profiles = next;
        }
        finally { _writes.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var next = Profiles.Where(profile => profile.Id != id).ToImmutableArray();
            await _storage.SaveAsync(next, cancellationToken);
            lock (_gate) _profiles = next;
        }
        finally { _writes.Release(); }
    }
}
