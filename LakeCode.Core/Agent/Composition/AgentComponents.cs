using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Session;
using LakeCode.Core.Agent.Session.History;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Tools.Eval;
using LakeCode.Core.Agent.Tools.Execution;

namespace LakeCode.Core.Agent.Composition;

/// <summary>Session-scoped composition result and resource owner, not an execution context.</summary>
internal sealed class AgentComponents(
    string cacheSessionId,
    WorkspaceFiles files,
    AgentLoop loop,
    AgentSessionState session,
    AgentHistory history,
    ToolInvocationRunner invocations,
    EvalTool? eval,
    DelegationCoordinator? delegation) : IDisposable, IAsyncDisposable
{
    private int _disposed;
    public string CacheSessionId { get; } = cacheSessionId;
    public WorkspaceFiles Files { get; } = files;
    public AgentLoop Loop { get; } = loop;
    public AgentSessionState Session { get; } = session;
    public AgentHistory History { get; } = history;
    public ToolInvocationRunner Invocations { get; } = invocations;
    public EvalTool? Eval { get; } = eval;
    public DelegationCoordinator? Delegation { get; } = delegation;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Delegation is not null) await Delegation.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { Eval?.Dispose(); }
            finally { History.Capture.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Delegation?.Dispose(); }
        finally
        {
            try { Eval?.Dispose(); }
            finally { History.Capture.Dispose(); }
        }
    }
}
