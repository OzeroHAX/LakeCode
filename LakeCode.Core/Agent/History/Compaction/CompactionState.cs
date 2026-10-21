namespace LakeCode.Core.Agent.Compaction;

// IDs are application-owned, scoped to a session branch, and never reused after rewind.
internal sealed record HistoryBlock(string Id, int Start, int End, string Hash);
internal sealed record HistoryReference(string BlockId, int StartLine, int EndLine);
internal sealed record SummaryEntry(string Kind, string Text, HistoryReference[] Sources);
// Entries are retained only for reading checkpoints produced by prompt v1.
// Prompt v2 returns free-form Text and an optional list of exact artifacts to retain.
internal sealed record CompactionSummary(SummaryEntry[]? Entries = null, string[]? PreserveVerbatim = null, string? Text = null);
internal sealed record CompactionUsage(long? Input, long? Output, long? Total);
internal sealed record CompactionCheckpoint(
    string Id, int CoveredMessages, CompactionSummary Summary, string[] KeptBlocks,
    string? ProtectedPlan, string ProviderId, string ModelId, string ReasoningEffort,
    DateTimeOffset CreatedAt, long BeforeBytes, long AfterBytes, CompactionUsage Usage,
    int PromptVersion = 1);
internal sealed record CompactionState(HistoryBlock[] Blocks, CompactionCheckpoint? Checkpoint)
{
    public static CompactionState Empty => new([], null);
}

/// <summary>Safe presentation data. No credentials, raw provider state or mutable history escapes Core.</summary>
public sealed record CompactionView(string Id, string Markdown, string ModelId, DateTimeOffset CreatedAt,
    long BeforeBytes, long AfterBytes, long? InputTokens, long? OutputTokens, long? TotalTokens);
