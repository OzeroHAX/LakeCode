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
        Action<ToolCallUpdate> updateCall, Action<ToolCallUpdate> publish, CancellationToken cancellationToken, string? manualDescription = null)
    {
        using var args = JsonDocument.Parse(call.Arguments);
        var root = args.RootElement;
        var descriptionText = manualDescription;
        if (descriptionText is null && call.Name is ("edit_file" or "write_file"))
        {
            descriptionText = StructuredFileDescription(root);
            if (descriptionText is null)
                return "Error: file changes require all seven rationale fields: intent, whyThisFile, evidence, expectedChange, alternativesConsidered, risksAndSideEffects, and verification.";
        }
        else if (descriptionText is null &&
                 (!root.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
                  string.IsNullOrWhiteSpace(descriptionText = description.GetString())))
        {
            return "Error: description is required: explain the action, purpose, necessity, alternatives, expected result, and side effects.";
        }
        if (descriptionText is not null && call.Name is ("bash" or "eval"))
        {
            var requiredSections = new[]
            {
                "Action:", "Purpose:", "Why needed now:", "Alternatives considered:",
                "Expected result:", "Side effects:"
            };
            var missingSections = requiredSections
                .Where(section => descriptionText.IndexOf(section, StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray();
            if (missingSections.Length > 0)
                return "Error: description must include these sections: " + string.Join(", ", missingSections);
        }
        if (call.Name is ("bash" or "eval") &&
            (!root.TryGetProperty(call.Name == "bash" ? "command" : "code", out var code) ||
             code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString())))
            return "Error: Nonempty command/code is required.";
        if (!requiresApproval) return "";

        var error = "";
        using (call.CallId.StartsWith("eval-", StringComparison.Ordinal) ? eval?.PauseTimeout() : null)
        {
            var request = new ExecutionApprovalRequest(call.CallId, call.Name, descriptionText!, call.Arguments);
            var assessment = manualDescription is not null
                ? new ExecutionApproval.ExecutionAssessment(ExecutionApproval.ExecutionDecision.AskUser, "Политика MCP-сервера: ручное подтверждение каждого вызова.")
                : AssessExecution is null
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

    private static string? StructuredFileDescription(JsonElement root)
    {
        var fields = new[]
        {
            (Label: "Intent:", Name: "intent"),
            (Label: "Why this file:", Name: "whyThisFile"),
            (Label: "Evidence:", Name: "evidence"),
            (Label: "Expected change:", Name: "expectedChange"),
            (Label: "Alternatives considered:", Name: "alternativesConsidered"),
            (Label: "Risks and side effects:", Name: "risksAndSideEffects"),
            (Label: "Verification:", Name: "verification")
        };
        var values = fields.Select(field =>
        {
            if (!root.TryGetProperty(field.Name, out var value) || value.ValueKind != JsonValueKind.String)
                return (field.Label, Value: (string?)null);
            var text = value.GetString()?.Trim();
            return (field.Label, Value: string.IsNullOrWhiteSpace(text) ? null : text);
        }).ToArray();
        return values.Any(value => value.Value is null)
            ? null
            : string.Join("\n", values.Select(value => $"{value.Label} {value.Value}"));
    }
}
