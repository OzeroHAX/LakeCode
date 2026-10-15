using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;
using LakeCode.Core.Attachments;

namespace LakeCode.Core.Conversations;

public sealed record RequestStatistics(ModelInfo Model, long? InputTokens, long? OutputTokens,
    long? TotalTokens, long? CachedInputTokens, long? ReasoningTokens);
public sealed record PendingMessageState(string Text, int ImageCount, string ModelId,
    string ReasoningEffort, SubmissionMode Mode, string ProviderId, int FileCount, AgentMode AgentMode = AgentMode.Build);

/// <summary>Current operational state, immediately readable without waiting for an event.
/// Transcript snapshots are available separately through ConversationService.GetConversation.</summary>
public sealed record ConversationState(long Revision, string? SessionId, string? Title, int TurnCount,
    bool IsBusy, bool IsGenerating, bool QueuePaused, bool SaveFailed, bool ContextFailed,
    bool PendingSaveFailed, bool CanStopGeneration, bool CanRewind,
    ImmutableArray<PendingMessageState> PendingMessages, ModelInfo StatisticsModel,
    RequestStatistics? Statistics, double? TokensPerSecond)
{
    public AgentMode? ActiveMode { get; init; }
    public ImplementationPlan? Plan { get; init; }
    public bool CanExecutePlan { get; init; }
    public SideQuestionState SideQuestions { get; init; } = new(0, false, []);
    public ImmutableArray<Agent.Delegation.DelegationSnapshot> Delegations { get; init; } = [];
}

public sealed record TranscriptItem(string Kind, string? Text, ToolCallUpdate? ToolCall);
public sealed record TurnView(string Text, ImmutableArray<ImageData> Images,
    ImmutableArray<TranscriptItem> Items, string Status, string? Error, ImmutableArray<FileAttachment> Files, ImmutableArray<FileMention> Mentions, bool IsBackgroundContinuation = false, AgentMode AgentMode = AgentMode.Build);
public sealed record ConversationView(string? SessionId, string? ModelId, string? ReasoningEffort,
    ImmutableArray<TurnView> Turns, string DraftText, ImmutableArray<ImageData> DraftImages, string? ProviderId,
    ImmutableArray<FileAttachment> DraftFiles, ImmutableArray<FileMention> DraftMentions);
