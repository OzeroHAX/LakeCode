using System.Collections.Immutable;
using System.Text.RegularExpressions;
using LakeCode.Core.Models;

namespace LakeCode.Core.Providers.Custom;

internal sealed record TypeSafeProvider() : ApiKeyProvider("typesafe", "TypeSafe · System One", "https://api.typesafe.ai/v1", null, "", [], null, 2)
{
    public override string ProtocolLabel => "Модели решений";
    public override bool SupportsChat => false;
    public override bool SupportsClassification => true;
    public override string DecisionModelsHint => "Доступны встроенные версии и ручные ID; алиасы не назначаются.";
    internal override string ModelsCollection => "models";
    internal override string ModelIdField => "name";
    public override ImmutableArray<ResolvedProviderModel> ModelEntries(ProviderProfile profile) => [];
    public override ImmutableArray<DecisionModelInfo> BuiltInDecisionModels =>
        [new("jev-1.14.0", true), new("jev-1.13.0", true)];
    public override ImmutableArray<DecisionModelInfo> DecisionModels(ProviderProfile profile) =>
        BuiltInDecisionModels.Select(model => model.Id).Concat(profile.Discovery?.Ids ?? []).Concat(profile.ModelOverrides.Where(item => item.Value.Manual).Select(item => item.Key))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new DecisionModelInfo(id, CanSelectDecisionModel(id))).ToImmutableArray();
    public override bool CanSelectDecisionModel(string id) => id is { Length: > 0 and <= 128 } &&
        Regex.IsMatch(id, @"\Ajev-\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public override DecisionModel CreateDecisionModel(ProviderProfile profile, string id) => CanSelectDecisionModel(id)
        ? new SystemOneModel(profile, id, new Uri(BaseUrl.TrimEnd('/') + "/systemone"))
        : throw new InvalidOperationException("Для Jev выберите фиксированный ID версии.");
    internal override void ValidateProfile(ProviderProfile profile)
    {
        foreach (var (id, rule) in profile.ModelOverrides)
            if (!CanSelectDecisionModel(id) || !rule.Manual || rule.Definition is not null ||
                rule.Overrides is not null || rule.CatalogModelId is not null)
                throw new InvalidDataException("Для классификатора задайте фиксированный ID jev-X.Y.Z.");
    }
}
