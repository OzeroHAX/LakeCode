using System.Collections.Immutable;

namespace LakeCode.Core.Agent;

public enum ModelModality
{
    Text,
    Image
}

/// <summary>API identifier, display name, documented token limits and supported capabilities.</summary>
public sealed record ModelInfo(
    string Id,
    string Name,
    int ContextWindowTokens,
    int MaxInputTokens,
    int MaxOutputTokens,
    ImmutableArray<ModelModality> InputModalities,
    ImmutableArray<ModelModality> OutputModalities,
    ImmutableArray<string> ReasoningEfforts);
