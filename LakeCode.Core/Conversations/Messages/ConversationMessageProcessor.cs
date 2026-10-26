using System.Diagnostics;
using LakeCode.Core.Agent;
using LakeCode.Core.Conversations.Projection;
using LakeCode.Core.Sessions;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Conversations.Messages;

/// <summary>Applies individual response contents to one turn; never consumes or schedules an agent stream.</summary>
internal sealed class ConversationMessageProcessor(SessionTurn turn, ModelInfo model, string reasoningEffort)
{
    private long? _firstTokenTimestamp;
    private long? _outputTokens;
    public bool GotAnswer { get; private set; }
    internal readonly record struct AppliedContent(ConversationEvent? Event, RequestStatistics? Statistics);

    // Called by the sole stream reader before taking the state lock, excluding lock contention from arrival time.
    public void ObserveArrival(AIContent content)
    {
        if (_firstTokenTimestamp is null && (content is TextReasoningContent { Text.Length: > 0 } || content is TextContent { Text.Length: > 0 }))
            _firstTokenTimestamp = Stopwatch.GetTimestamp();
    }

    // Called under the conversation lock so transcript mutation and publication retain their order.
    public AppliedContent Apply(AIContent content, bool startsNewReasoningBlock = false)
    {
        if (content is UsageContent usage)
        {
            var details = usage.Details;
            _outputTokens = details.OutputTokenCount ?? _outputTokens;
            turn.Usage = new SavedUsage(details.InputTokenCount, details.CachedInputTokenCount,
                details.OutputTokenCount, details.ReasoningTokenCount, details.TotalTokenCount);
            return new AppliedContent(null, ConversationSnapshotMapper.ToStatistics(turn.Usage, model));
        }
        if (reasoningEffort != "none" && content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
        {
            turn.AppendText("thinking", reasoning.Text, startsNewReasoningBlock);
            return new AppliedContent(new ReasoningDelta(reasoning.Text, startsNewReasoningBlock), null);
        }
        if (content is TextContent answer && !string.IsNullOrEmpty(answer.Text))
        {
            GotAnswer = true;
            turn.AppendText("text", answer.Text);
            return new AppliedContent(new TextDelta(answer.Text), null);
        }
        return default;
    }

    // Capture before serialization so persistence time is not included in generation throughput.
    public double? TokensPerSecond
    {
        get
        {
            var elapsed = _firstTokenTimestamp is { } start ? Stopwatch.GetElapsedTime(start) : TimeSpan.Zero;
            return _outputTokens is { } tokens && elapsed > TimeSpan.Zero ? tokens / elapsed.TotalSeconds : null;
        }
    }
}
