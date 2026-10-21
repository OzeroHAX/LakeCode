using System.Text.Json;
using LakeCode.Core.Agent.Tools.Eval;

namespace LakeCode.Core.Agent.Tools.Execution;

/// <summary>Execution assessment and per-call user approval, shared by MAF and eval invocations.</summary>
internal sealed class ToolExecutionApproval
{
    internal Func<ExecutionApprovalRequest, CancellationToken, Task<bool>>? ConfirmExecution { get; init; }
    internal Func<ExecutionApprovalRequest, CancellationToken, Task<ExecutionApproval.ExecutionAssessment>>? AssessExecution { get; init; }
    internal Func<bool>? IsExecutionClassifierEnabled { get; init; }

    public bool ClassifierEnabled => IsExecutionClassifierEnabled?.Invoke() == true;

    public async Task<string> CheckAsync(ToolCallUpdate call, bool requiresApproval, EvalTool? eval,
        Action<ToolCallUpdate> updateCall, Action<ToolCallUpdate> publish, CancellationToken cancellationToken)
    {
        using var args = JsonDocument.Parse(call.Arguments);
        var root = args.RootElement;
        if (!root.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(description.GetString()))
            return "Error: description is required: explain action, purpose and expected side effects.";
        if (call.Name is ("bash" or "eval") &&
            (!root.TryGetProperty(call.Name == "bash" ? "command" : "code", out var code) ||
             code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString())))
            return "Error: Nonempty command/code is required.";
        if (!requiresApproval) return "";

        var error = "";
        using (call.CallId.StartsWith("eval-", StringComparison.Ordinal) ? eval?.PauseTimeout() : null)
        {
            var request = new ExecutionApprovalRequest(call.CallId, call.Name, description.GetString()!, call.Arguments);
            var assessment = AssessExecution is null
                ? new ExecutionApproval.ExecutionAssessment(ExecutionApproval.ExecutionDecision.AskUser, "Классификатор недоступен.")
                : await AssessExecution(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            call = call with { Assessment = assessment };
            updateCall(call);
            if (assessment.Decision == ExecutionApproval.ExecutionDecision.Deny)
                error = "Error: Execution denied by policy. " + assessment.Reason + " Action was not executed. Do not bypass through another tool.";
            else if (assessment.Decision == ExecutionApproval.ExecutionDecision.AskUser)
            {
                publish(call with { Status = ToolCallStatus.AwaitingApproval });
                var allowed = ConfirmExecution is not null && await ConfirmExecution(request with { Assessment = assessment }, cancellationToken);
                call = call with { Assessment = assessment with { ManualAllowed = allowed } };
                updateCall(call);
                if (!allowed) error = "Error: Execution denied by user or approval unavailable. Action was not executed. Do not retry through another tool.";
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (error.Length == 0) publish(call with { Status = ToolCallStatus.Running });
        return error;
    }
}
