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

    public ModelInfo GetModel(string providerId, string modelId) => Get(providerId).Models.FirstOrDefault(model => model.Id == modelId)
        ?? throw new InvalidOperationException("Модель недоступна у выбранного провайдера. Проверьте /models.");

    public async Task SaveAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
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
