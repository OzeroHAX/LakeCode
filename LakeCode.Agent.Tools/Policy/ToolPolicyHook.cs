using LakeCode.Agent;

namespace LakeCode.Agent.Tools.Policy;

/// <summary>
/// Who can approve a tool call. Orchestration supplies these; the tools project only knows that a
/// confirmation may be required, never how the question reaches the user.
/// </summary>
public sealed record ToolPolicyConfirmations(
    Func<AgentToolCall, CancellationToken, ValueTask<bool>>? Process = null,
    Func<AgentToolCall, CancellationToken, ValueTask<bool>>? WorkspaceWrite = null)
{
    public static ToolPolicyConfirmations None { get; } = new();
}

/// <summary>
/// Builds the pre-tool hook that enforces mode restrictions. Effects come from
/// <see cref="AgentToolCatalog"/> rather than tool names, so a new capability is covered by declaring
/// its effects instead of editing policy code.
/// </summary>
public static class ToolPolicyHook
{
    /// <summary>
    /// A tool that declares no effects is denied unless the mode explicitly allows uncovered tools:
    /// an undeclared capability must never be treated as safe.
    /// </summary>
    public static Func<AgentHookContext, AgentToolCall, CancellationToken, ValueTask<AgentToolDecision>> Create(
        AgentToolCatalog catalog,
        ToolPolicyMode mode,
        ToolPolicyConfirmations? confirmations = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mode);

        var confirmed = confirmations ?? ToolPolicyConfirmations.None;

        return (_, call, cancellationToken) => DecideAsync(catalog, mode, call, confirmed, cancellationToken);
    }

    public static async ValueTask<AgentToolDecision> DecideAsync(
        AgentToolCatalog catalog,
        ToolPolicyMode mode,
        AgentToolCall call,
        ToolPolicyConfirmations? confirmations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(call);

        if (!catalog.TryGet(call.ToolName, out var metadata))
        {
            return AgentToolDecision.Deny($"Tool '{call.ToolName}' is not available in this run.");
        }

        if (metadata.Effects == AgentToolEffects.None)
        {
            return mode.AllowUncoveredTools
                ? AgentToolDecision.Allow
                : AgentToolDecision.Deny(
                    $"Tool '{call.ToolName}' declares no effects, so it cannot be allowed by policy.");
        }

        if (metadata.WritesWorkspace && !mode.AllowWrites)
        {
            return AgentToolDecision.Deny(
                $"Tool '{call.ToolName}' writes to the workspace, which this mode forbids.");
        }

        if (metadata.ExecutesProcess && !mode.AllowProcess)
        {
            return AgentToolDecision.Deny(
                $"Tool '{call.ToolName}' starts a process, which this mode forbids.");
        }

        if (metadata.CallsExternalService && !mode.AllowExternalService)
        {
            return AgentToolDecision.Deny(
                $"Tool '{call.ToolName}' calls an external service, which this mode forbids.");
        }

        if (metadata.PromptsUser && !mode.AllowUserPrompts)
        {
            return AgentToolDecision.Deny(
                $"Tool '{call.ToolName}' asks the user, which this mode forbids.");
        }

        var confirmed = confirmations ?? ToolPolicyConfirmations.None;

        if (metadata.ExecutesProcess && mode.RequireApprovalForProcess)
        {
            return await ApprovalAsync(
                    call,
                    confirmed.Process,
                    "Process execution was not approved.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (metadata.WritesWorkspace && mode.RequireApprovalForWrites)
        {
            return await ApprovalAsync(
                    call,
                    confirmed.WorkspaceWrite,
                    "Workspace write was not approved.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return AgentToolDecision.Allow;
    }

    private static async ValueTask<AgentToolDecision> ApprovalAsync(
        AgentToolCall call,
        Func<AgentToolCall, CancellationToken, ValueTask<bool>>? confirm,
        string denialReason,
        CancellationToken cancellationToken)
    {
        if (confirm is null)
        {
            // Without a confirmation channel, a required approval cannot be granted.
            return AgentToolDecision.Deny(denialReason);
        }

        return await confirm(call, cancellationToken).ConfigureAwait(false)
            ? AgentToolDecision.Allow
            : AgentToolDecision.Deny(denialReason);
    }
}
