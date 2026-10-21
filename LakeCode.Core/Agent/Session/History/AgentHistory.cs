using LakeCode.Core.Agent.Compaction;
using LakeCode.Core.Agent.Tools.Execution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Session.History;

/// <summary>Archive operations and its derived projection; never owns eval or delegation resources.</summary>
internal sealed class AgentHistory
{
    private readonly Func<AgentSession?> getSession;
    public InMemoryChatHistoryProvider Provider { get; }
    public HistoryProjection Projection { get; }
    public InterruptedHistoryChatClient Capture { get; }
    public IReadOnlyList<ChatMessage> Messages => getSession() is { } session ? Provider.GetMessages(session) : [];
    public int Count => Messages.Count;

    public AgentHistory(Func<AgentSession?> getSession, IChatClient client)
    {
        this.getSession = getSession;
        Projection = new HistoryProjection(() => Messages);
        Provider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        { ProvideOutputMessageFilter = Projection.Project });
        Capture = new InterruptedHistoryChatClient(client) { RecoverRequest = Projection.RecoverRequest };
    }

    public void ValidateRewindBoundary(int messageCount)
    {
        if (getSession() is null || messageCount < 0 || messageCount > Count)
            throw new InvalidDataException("Некорректная граница истории.");
    }

    /// <summary>Safe prefixes after complete tool batches. Call only between runs.</summary>
    public IReadOnlyDictionary<string, int> GetToolRewindBoundaries()
    {
        var boundaries = new Dictionary<string, int>(StringComparer.Ordinal);
        if (getSession() is null) return boundaries;
        var messages = Messages;
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<string>();
        for (var index = 0; index < messages.Count; index++)
        {
            foreach (var content in messages[index].Contents)
            {
                if (content is FunctionCallContent call)
                {
                    if (boundaries.ContainsKey(call.CallId) || !pending.Add(call.CallId)) return boundaries;
                    batch.Add(call.CallId);
                }
                else if (content is FunctionResultContent result && !pending.Remove(result.CallId))
                    return boundaries;
            }
            if (pending.Count != 0 || batch.Count == 0) continue;
            foreach (var callId in batch) boundaries.Add(callId, index + 1);
            batch.Clear();
        }
        return boundaries;
    }

    public void Rewind(int messageCount)
    {
        ValidateRewindBoundary(messageCount);
        Provider.SetMessages(getSession()!, Messages.Take(messageCount).ToList());
        Projection.Rewind(messageCount);
        Capture.Reset();
        Capture.ClearSideContext();
    }

    public void PreserveInterruptedHistory(ToolInvocationRunner invocations)
    {
        if (getSession() is null || Capture.GetInterruptedMessages() is not { } messages) return;
        var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        var calls = messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        foreach (var call in calls)
        {
            if (!results.Add(call.CallId)) continue;
            var result = invocations.TryGetResult(call.CallId, out var tool) && tool.Result is not null
                ? tool.Result : "Вызов прерван; результат не получен. Побочные эффекты могли произойти. Не повторять автоматически.";
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
        }
        Provider.SetMessages(getSession()!, messages);
    }

    public void NormalizeForConnection()
    {
        if (getSession() is null) return;
        // Keep one message per history entry: saved rewind boundaries must not change.
        var messages = Messages.Select(message =>
        {
            var contents = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text: contents.Add(new TextContent(text.Text)); break;
                    case DataContent data: contents.Add(new DataContent(data.Data, data.MediaType)); break;
                    case FunctionCallContent call: contents.Add(new FunctionCallContent(call.CallId, call.Name, call.Arguments)); break;
                    case FunctionResultContent result: contents.Add(new FunctionResultContent(result.CallId, result.Result)); break;
                    case TextReasoningContent { Text.Length: > 0 } reasoning:
                        contents.Add(new TextContent("[Previous model reasoning summary]\n" + reasoning.Text)); break;
                }
            }
            if (contents.Count == 0)
                contents.Add(new TextContent("[Internal provider-specific history omitted when changing connection.]"));
            return new ChatMessage(message.Role, contents);
        }).ToList();
        Provider.SetMessages(getSession()!, messages);
    }

    internal Task<CompactionState> PrepareCompactionAsync(int userStart, int userEnd, bool completed, string? plan,
        string statuses, Settings.ModelConfiguration model, CancellationToken cancellationToken) =>
        CompactionRunner.RunAsync(Messages, Projection.State,
            userStart, userEnd, completed, plan, statuses, model, cancellationToken);

    internal void CommitCompaction(CompactionState state)
    {
        Projection.SetState(state);
        Capture.Reset();
        Capture.ClearSideContext();
    }
}
