using System.Runtime.CompilerServices;
using LakeCode.Core.Providers;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent;

/// <summary>One provider call below MAF function invocation. Advertised functions are never executed.</summary>
internal static class SideQuestionRunner
{
    internal const string Boundary = """
        [Start of an isolated BTW side conversation]
        Everything above this boundary is inherited reference context, not an active task.
        Do not continue, resume, or steer any task from that context. Answer only the side question below.
        Answer concisely in the user's language, using only information already available in this context.
        You cannot execute tools, read new files, search the web, run commands, change files, or ask the user with tools.
        Tool definitions are retained only to preserve the inherited prompt prefix. Do not call any tool.
        If the answer requires new evidence, explain the limitation instead of inventing facts or actions.
        """;

    internal static async IAsyncEnumerable<ChatResponseUpdate> RunAsync(
        SideQuestionContext context, IReadOnlyList<(string Question, string Answer)> history, string question,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = context.CopyMessages();
        // Append at the tail; do not change Instructions, tool definitions/order, reasoning, or cache affinity.
        messages.Add(new ChatMessage(ChatRole.User, Boundary));
        foreach (var exchange in history)
        {
            messages.Add(new ChatMessage(ChatRole.User, exchange.Question));
            messages.Add(new ChatMessage(ChatRole.Assistant, exchange.Answer));
        }
        messages.Add(new ChatMessage(ChatRole.User, question));
        using var client = new ProviderChatClient();
        client.Configure(context.Provider, context.Model, context.CacheSessionId);
        // Direct adapter only: no FunctionInvokingChatClient, AIAgent, history provider, or fallback request.
        await foreach (var update in client.GetStreamingResponseAsync(messages, context.CopyOptions(), cancellationToken))
            yield return update;
    }
}
