using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LakeCode.Cli.Agent;

/// <summary>Captures the last provider request and its partial response, below function invocation.</summary>
internal sealed class InterruptedHistoryChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private List<ChatMessage>? _request;
    private readonly List<ChatResponseUpdate> _updates = [];

    public void Reset()
    {
        _request = null;
        _updates.Clear();
    }

    public List<ChatMessage>? GetInterruptedMessages()
    {
        if (_request is null) return null;
        return [.. _request.Where(message => message.Role != ChatRole.System),
            .. _updates.ToChatResponse().Messages];
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _request = messages.Select(message => message.Clone()).ToList();
        _updates.Clear();
        await foreach (var update in base.GetStreamingResponseAsync(_request, options, cancellationToken))
        {
            _updates.Add(update.Clone());
            yield return update;
        }
    }
}
