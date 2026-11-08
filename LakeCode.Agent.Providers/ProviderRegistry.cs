using LakeCode.Agent;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers;

public sealed class ProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IProvider> _providers;

    public ProviderRegistry(IEnumerable<IProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers
            .ToDictionary(provider => provider.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IProvider> All =>
        _providers.Values.ToArray();

    public IProvider Get(string id) =>
        _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"Provider '{id}' is not registered.");

    public IAgentModel Resolve(string providerAndModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAndModel);

        var separator = providerAndModel.IndexOf('/');
        if (separator <= 0 || separator == providerAndModel.Length - 1)
        {
            throw new ArgumentException(
                "Model reference must have the form 'provider/model'.",
                nameof(providerAndModel));
        }

        var providerId = providerAndModel[..separator];
        var modelId = providerAndModel[(separator + 1)..];

        return Get(providerId).GetModel(modelId);
    }
}
