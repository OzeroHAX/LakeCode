using System.Runtime.CompilerServices;
using LakeCode.Core.Agent.Execution;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Mcp;

/// <summary>Updates both the advertised schemas and executor catalog at each inner model request.</summary>
internal sealed class McpToolsChatClient(IChatClient inner, McpSession mcp, Func<AgentConfiguration> configuration) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is not null) await mcp.ApplyAsync(options, configuration(), cancellationToken);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options is not null) await mcp.ApplyAsync(options, configuration(), cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)) yield return update;
    }
}
