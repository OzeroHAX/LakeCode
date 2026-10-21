using Microsoft.Agents.AI;

namespace LakeCode.Core.Agent;

/// <summary>
/// Hides provider-supplied instructions and tools while the repository-free Research mode is active.
/// Project skills live in the workspace, so advertising them there would leak repository content.
/// </summary>
internal sealed class ResearchAwareContextProvider(AIContextProvider inner, Func<bool> isResearch) : AIContextProvider
{
    // Keep the inner provider's session state keys so restored sessions see the same provider identity.
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    // Full control over the merge: the base implementation would merge the additional context on
    // top of the inner provider's already merged result. Research returns the caller's context
    // unchanged, so instructions, tools and messages stay as supplied without any skills context.
    protected override ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken) =>
        isResearch() ? ValueTask.FromResult(context.AIContext) : inner.InvokingAsync(context, cancellationToken);

    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken) =>
        inner.InvokedAsync(context, cancellationToken);
}
