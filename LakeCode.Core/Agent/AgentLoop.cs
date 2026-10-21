using System.Runtime.CompilerServices;
using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Session;
using Microsoft.Agents.AI;

namespace LakeCode.Core.Agent;

/// <summary>Runs one turn against one session; consume each response before starting the next turn.</summary>
internal sealed class AgentLoop(
    AIAgent agent,
    AgentSessionState session,
    AgentRunPreparer preparer,
    DelegationCoordinator? delegation)
{
    public async IAsyncEnumerable<AgentResponseUpdate> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            preparer.BeginTurn(request);
            var currentSession = await session.GetOrCreateAsync(cancellationToken);
            var prepared = preparer.Prepare(request, session);
            var updates = agent.RunStreamingAsync(prepared.Message, currentSession,
                options: prepared.Options, cancellationToken: cancellationToken);
            await foreach (var update in ReasoningSummaryFormatter.FormatAsync(updates, cancellationToken))
                yield return update;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && delegation is not null)
                await delegation.CancelPendingAsync().ConfigureAwait(false);
        }
    }
}
