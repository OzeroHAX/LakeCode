using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Tools;

namespace LakeCode.Core.Conversations.Interactions;

/// <summary>Typed waits for read and execution decisions; contains no file or execution policy.</summary>
internal sealed class ConversationApprovals
{
    private readonly Dictionary<Guid, PendingInteraction<ExternalReadDecision>> _reads = [];
    private readonly Dictionary<Guid, PendingInteraction<bool>> _executions = [];
    public int ReadCount => _reads.Count;

    public ReadPermissionRequested RegisterRead(PendingInteraction<ExternalReadDecision> pending, ExternalReadRequest request)
    {
        _reads.Add(pending.Id, pending);
        return new ReadPermissionRequested(pending.Id, request);
    }

    public ExecutionApprovalRequested RegisterExecution(PendingInteraction<bool> pending, ExecutionApprovalRequest request)
    {
        _executions.Add(pending.Id, pending);
        return new ExecutionApprovalRequested(pending.Id, request);
    }

    public void RemoveRead(Guid id) => _reads.Remove(id);
    public void RemoveExecution(Guid id) => _executions.Remove(id);

    public bool ResolveRead(ResolveReadPermission permission) => Enum.IsDefined(permission.Decision) &&
        _reads.TryGetValue(permission.InteractionId, out var pending) && pending.Resolve(permission.Decision);

    public bool ResolveExecution(ResolveExecutionApproval approval) =>
        _executions.TryGetValue(approval.InteractionId, out var pending) && pending.Resolve(approval.Allow);

    public void AllowPendingReads()
    {
        foreach (var pending in _reads.Values) pending.Resolve(ExternalReadDecision.AllowSession);
    }

    public bool Fail(Guid id, Exception error)
    {
        if (_reads.TryGetValue(id, out var read)) return read.Fail(error);
        return _executions.TryGetValue(id, out var execution) && execution.Fail(error);
    }
}
