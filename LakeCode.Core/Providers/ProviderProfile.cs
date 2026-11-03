using System.Collections.Immutable;
using System.Text.Json.Serialization;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Providers;

/// <summary>Immutable user settings captured when a request is accepted.</summary>
public sealed record ProviderProfile
{
    [JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Revision { get; init; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string DefinitionId { get; init; } = "";
    [JsonRequired] public ImmutableDictionary<string, string> Credentials { get; init; } = ImmutableDictionary<string, string>.Empty;
    [JsonRequired] public ImmutableDictionary<string, ModelOverride> ModelOverrides { get; init; } = ImmutableDictionary<string, ModelOverride>.Empty;
    [JsonIgnore] public DiscoveredModels? Discovery { get; init; }
    [JsonIgnore] internal IProviderCredentialState? CredentialState { get; init; }
    [JsonIgnore] public Provider Definition => ProviderCatalog.Get(DefinitionId);
    [JsonIgnore] public bool HasCredentials => Definition.HasCredentials(this);
    [JsonIgnore] public ImmutableArray<ResolvedProviderModel> ModelEntries => Definition.ModelEntries(this);
    [JsonIgnore] public ImmutableArray<ModelInfo> Models => ModelEntries
        .Where(item => item.Available && item.Model is not null).Select(item => item.Model!).ToImmutableArray();

    public override string ToString() => Name;
    public Task<DiscoveredModels> DiscoverAsync(CancellationToken cancellationToken = default) => Definition.DiscoverAsync(this, cancellationToken);
    internal void Authorize(HttpRequestMessage request) => Definition.Authorize(this, request);
    internal IEnumerable<string> Secrets => Definition.Secrets(this);
    internal string Redact(string text) => Definition.Redact(this, text);

    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "D", out _) || !Guid.TryParseExact(Revision, "D", out _) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(DefinitionId) || DefinitionId.Length > 100 || DefinitionId.Any(char.IsControl))
            throw new InvalidDataException("Некорректный профиль провайдера.");
        var provider = Definition;
        if (Credentials is null || Credentials.Count > 16 || Credentials.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 100 || item.Key.Any(char.IsControl) ||
                item.Value is null || item.Value.Length > 16384 || item.Value.Any(char.IsControl)))
            throw new InvalidDataException("Некорректные учётные данные провайдера.");
        if (ModelOverrides is null || ModelOverrides.Count > ProviderModel.MaxModels ||
            ModelOverrides.Any(item => !ProviderModel.ValidId(item.Key) || item.Value is null))
            throw new InvalidDataException("Некорректные настройки моделей.");
        if (Discovery is { } discovery && (discovery.Ids.IsDefault || discovery.Ids.Length > ProviderModel.MaxModels ||
            discovery.Ids.Any(id => !ProviderModel.ValidId(id)) || discovery.Ids.Distinct(StringComparer.Ordinal).Count() != discovery.Ids.Length ||
            discovery.DecisionIds.IsDefault || discovery.DecisionIds.Length > ProviderModel.MaxModels ||
            discovery.DecisionIds.Any(id => !ProviderModel.ValidId(id)) || discovery.DecisionIds.Distinct(StringComparer.Ordinal).Count() != discovery.DecisionIds.Length ||
            discovery.Capabilities is null || discovery.Capabilities.Count > ProviderModel.MaxModels ||
            discovery.Capabilities.Any(item => !discovery.Ids.Contains(item.Key, StringComparer.Ordinal) || item.Value is null)))
            throw new InvalidDataException("Некорректный список обнаруженных моделей.");
        provider.ValidateProfile(this);
    }

}
