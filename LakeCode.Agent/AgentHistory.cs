using System.Collections.Immutable;
using System.Text.Json;

namespace LakeCode.Agent;

/// <summary>
/// Sole owner of the conversation messages. The runner reads snapshots and writes commits; consumers
/// never mutate the list directly, so model context, rewind points and persistence cannot drift apart.
/// </summary>
public sealed class AgentHistory
{
    private readonly object _gate = new();
    private readonly List<AgentMessage> _messages = [];

    public AgentHistory()
    {
    }

    public AgentHistory(IEnumerable<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages.AddRange(messages);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    /// <summary>Copy of the current messages; safe to enumerate while a run is in progress.</summary>
    public IReadOnlyList<AgentMessage> Snapshot()
    {
        lock (_gate)
        {
            return _messages.ToArray();
        }
    }

    /// <summary>Rewind points keyed by tool call id, valid only after a complete tool batch.</summary>
    public IReadOnlyDictionary<string, int> GetToolRewindBoundaries()
    {
        lock (_gate)
        {
            return FindToolRewindBoundaries(_messages);
        }
    }

    public bool TryGetToolRewindBoundary(string toolCallId, out int messageCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        var boundaries = GetToolRewindBoundaries();
        if (boundaries.TryGetValue(toolCallId, out var boundary))
        {
            messageCount = boundary;
            return true;
        }

        messageCount = 0;
        return false;
    }

    /// <summary>Moves the rewind point for the call id to the boundary satisfied by completed batches.</summary>
    public bool TryRewindToToolCall(string toolCallId)
    {
        if (!TryGetToolRewindBoundary(toolCallId, out var messageCount))
        {
            return false;
        }

        Rewind(messageCount);
        return true;
    }

    /// <summary>Drops everything after the given message count. Only complete boundaries are allowed.</summary>
    public void Rewind(int messageCount)
    {
        lock (_gate)
        {
            ValidateBoundary(messageCount);
            _messages.RemoveRange(messageCount, _messages.Count - messageCount);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }

    internal void Add(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _messages.Add(message);
        }
    }

    internal void AddRange(IEnumerable<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_gate)
        {
            _messages.AddRange(messages);
        }
    }

    /// <summary>
    /// Adds error results for tool calls that never received one, so an interrupted run leaves a
    /// provider-valid history. Side effects of those calls are not undone or retried.
    /// </summary>
    public int CompleteInterruptedToolCalls(string resultText)
    {
        ArgumentNullException.ThrowIfNull(resultText);

        lock (_gate)
        {
            var completed = _messages.OfType<ToolResultMessage>()
                .Select(message => message.ToolCallId)
                .ToHashSet(StringComparer.Ordinal);
            var added = 0;

            foreach (var call in _messages.OfType<AssistantMessage>().SelectMany(message => message.ToolCalls))
            {
                if (!completed.Add(call.ToolCallId))
                {
                    continue;
                }

                _messages.Add(new ToolResultMessage(
                    AgentIds.Create(),
                    call.ToolCallId,
                    call.ToolName,
                    true,
                    resultText));
                added++;
            }

            return added;
        }
    }

    /// <summary>Serializable history that can be restored into a new session.</summary>
    public AgentHistorySnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            return new AgentHistorySnapshot(_messages.ToImmutableArray());
        }
    }

    public static AgentHistory Restore(AgentHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgentHistory(snapshot.Messages);
    }

    private void ValidateBoundary(int messageCount)
    {
        if (messageCount < 0 || messageCount > _messages.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(messageCount),
                messageCount,
                "The rewind boundary is outside the stored history.");
        }

        var pending = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < messageCount; index++)
        {
            switch (_messages[index])
            {
                case AssistantMessage assistant:
                    foreach (var call in assistant.ToolCalls)
                    {
                        pending.Add(call.ToolCallId);
                    }

                    break;

                case ToolResultMessage result:
                    pending.Remove(result.ToolCallId);
                    break;
            }
        }

        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                "The rewind boundary splits a tool call from its result; use a completed batch boundary.");
        }
    }

    private static IReadOnlyDictionary<string, int> FindToolRewindBoundaries(
        IReadOnlyList<AgentMessage> messages)
    {
        var boundaries = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<string>();

        for (var index = 0; index < messages.Count; index++)
        {
            switch (messages[index])
            {
                case AssistantMessage assistant:
                    foreach (var call in assistant.ToolCalls)
                    {
                        if (boundaries.ContainsKey(call.ToolCallId) || !pending.Add(call.ToolCallId))
                        {
                            return boundaries;
                        }

                        batch.Add(call.ToolCallId);
                    }

                    break;

                case ToolResultMessage result when !pending.Remove(result.ToolCallId):
                    return boundaries;
            }

            if (pending.Count != 0 || batch.Count == 0)
            {
                continue;
            }

            foreach (var callId in batch)
            {
                boundaries.Add(callId, index + 1);
            }

            batch.Clear();
        }

        return boundaries;
    }
}

/// <summary>Immutable history payload used for persistence and for creating a new session.</summary>
public sealed record AgentHistorySnapshot(ImmutableArray<AgentMessage> Messages)
{
    public static AgentHistorySnapshot Empty { get; } = new([]);
}
