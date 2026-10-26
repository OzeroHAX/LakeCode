using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace LakeCode.Core.Agent.Execution;

/// <summary>Separates independent Markdown reasoning summaries without buffering the response.</summary>
internal static class ReasoningSummaryFormatter
{
    public static (string ItemId, int SummaryIndex)? GetSummary(AgentResponseUpdate update)
    {
#pragma warning disable OPENAI001
        return update.AsChatResponseUpdate().RawRepresentation is StreamingResponseReasoningSummaryTextDeltaUpdate delta
            ? (delta.ItemId, delta.SummaryIndex)
            : null;
#pragma warning restore OPENAI001
    }

    public static async IAsyncEnumerable<AgentResponseUpdate> FormatAsync(
        IAsyncEnumerable<AgentResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (string ItemId, int SummaryIndex)? previousSummary = null;
        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
#pragma warning disable OPENAI001
            if (update.AsChatResponseUpdate().RawRepresentation is StreamingResponseReasoningSummaryTextDeltaUpdate delta)
            {
                var summary = (delta.ItemId, delta.SummaryIndex);
                if (previousSummary is { } previous && previous != summary)
                {
                    // Summary parts are independent Markdown; token deltas within a part are not.
                    foreach (var content in update.Contents.OfType<TextReasoningContent>())
                        content.Text = "\n\n" + content.Text;
                }
                previousSummary = summary;
            }
#pragma warning restore OPENAI001
            yield return update;
        }
    }
}
