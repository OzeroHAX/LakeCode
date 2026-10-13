using System.Collections.Immutable;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Conversations;

/// <summary>Ephemeral display state only; never included in SessionSnapshot or the main agent's history.</summary>
public sealed record SideQuestionExchange(Guid RequestId, string Question, string Answer,
    string ProviderName, ModelInfo Model, string ReasoningEffort, string Status,
    string? Error, RequestStatistics? Statistics);

public sealed record SideQuestionState(long Revision, bool IsGenerating, ImmutableArray<SideQuestionExchange> Exchanges);
