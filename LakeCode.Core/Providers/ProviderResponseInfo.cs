namespace LakeCode.Core.Providers;

/// <summary>Transport-level routing metadata emitted by a provider response policy.</summary>
public sealed record ProviderResponseRouting(string RequestedModelId,
    string? EffectiveModelId, bool IsFallback);
