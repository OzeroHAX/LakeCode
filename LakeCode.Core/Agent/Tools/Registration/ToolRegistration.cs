using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.Registration;

internal enum ToolExecutionPolicy { None, RequireApproval }

/// <summary>Settings belong to one registration, shared by direct and bridge invocations.</summary>
internal sealed class ToolRegistration(AIFunction function)
{
    public AIFunction Function { get; } = function;
    public ToolExecutionPolicy ExecutionPolicy { get; private set; }
    public bool IsEvalAllowed { get; private set; } = true;

    public ToolRegistration Policy(ToolExecutionPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        ExecutionPolicy = policy;
        return this;
    }

    public ToolRegistration EvalAllowed(bool allowed)
    {
        IsEvalAllowed = allowed;
        return this;
    }
}
