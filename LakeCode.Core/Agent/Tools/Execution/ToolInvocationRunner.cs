using System.Collections.Concurrent;
using System.Text.Json;
using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Tools.Eval;
using LakeCode.Core.Agent.Tools.Registration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.Execution;

/// <summary>One invocation path for model calls and the eval bridge, including policy and side-effect events.</summary>
internal sealed class ToolInvocationRunner(
    Func<AgentConfiguration> getConfiguration,
    Func<EvalTool?> getEval,
    Func<DelegationCoordinator?> getDelegation,
    Func<Settings.ImageGenerationConfiguration?> getImageGeneration,
    Action validateSkillRoots,
    ToolExecutionApproval approval)
{
    private readonly AsyncLocal<Action<FileChange>?> _fileChangeObserver = new();
    private readonly AsyncLocal<Action<BashExecution>?> _bashOutputObserver = new();
    private readonly AsyncLocal<ToolCallUpdate?> _delegationInvocation = new();
    private readonly AsyncLocal<IServiceProvider?> _services = new();
    private readonly ConcurrentDictionary<string, ToolCallUpdate> _results = new(StringComparer.Ordinal);

    public event Action<ToolCallUpdate>? ToolCallUpdated;
    public event Action<BashOutputUpdate>? BashOutputUpdated;
    public ToolCallUpdate? DelegationInvocation => _delegationInvocation.Value;
    public void ReportFileChange(FileChange change) => _fileChangeObserver.Value?.Invoke(change);
    public void ReportBashOutput(BashExecution execution) => _bashOutputObserver.Value?.Invoke(execution);
    public void BeginTurn() => _results.Clear();
    public bool TryGetResult(string callId, out ToolCallUpdate result) => _results.TryGetValue(callId, out result!);

    private void Publish(ToolCallUpdate update)
    {
        _results[update.CallId] = update;
        ToolCallUpdated?.Invoke(update);
    }

    public async ValueTask<object?> ObserveToolCallAsync(AIAgent agent, FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken cancellationToken)
    {
        var previousServices = _services.Value;
        _services.Value = context.Arguments.Services;
        try
        {
            var result = await InvokeAsync(context.CallContent.CallId, context.Function.Name,
                JsonSerializer.Serialize(context.Arguments), token => next(context, token), cancellationToken);
            // Native termination drains the stream and records the outer eval result as well.
            if (getConfiguration().IsComplete()) context.Terminate = true;
            return result;
        }
        finally { _services.Value = previousServices; }
    }

    public async Task<string> InvokeEvalToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!getConfiguration().Tools.TryGet(name, out var registration) || !registration.IsEvalAllowed)
            return $"Error: Tool '{name}' is not available through eval. Recursive eval is not supported.";
        if (arguments.ValueKind != JsonValueKind.Object) return "Error: Tool arguments must be an object.";
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText())!;
        var result = await InvokeAsync("eval-" + Guid.NewGuid().ToString("N"), name, arguments.GetRawText(),
            token => registration.Function.InvokeAsync(new AIFunctionArguments(values) { Services = _services.Value }, token), cancellationToken);
        return result switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => JsonSerializer.Serialize(result)
        };
    }

    private async ValueTask<object?> InvokeAsync(string callId, string name, string argumentsJson,
        Func<CancellationToken, ValueTask<object?>> invoke, CancellationToken cancellationToken)
    {
        var configuration = getConfiguration();
        var registered = configuration.Tools.TryGet(name, out var registration);
        var requiresApproval = registered && registration.ExecutionPolicy == ToolExecutionPolicy.RequireApproval;
        var call = new ToolCallUpdate(callId, name, argumentsJson,
            requiresApproval
                ? approval.ClassifierEnabled ? ToolCallStatus.Classifying : ToolCallStatus.AwaitingApproval
                : ToolCallStatus.Running);
        if (name != "delegate") Publish(call);
        FileChange? fileChange = null;
        BashExecution? bashExecution = null;
        DelegationSnapshot? delegation = null;
        var previousInvocation = _delegationInvocation.Value;
        if (name == "delegate") _delegationInvocation.Value = call;
        var previousObserver = _fileChangeObserver.Value;
        var previousBashObserver = _bashOutputObserver.Value;
        _fileChangeObserver.Value = change => fileChange = change;
        _bashOutputObserver.Value = execution =>
        {
            bashExecution = execution;
            BashOutputUpdated?.Invoke(new BashOutputUpdate(callId, execution));
        };
        try
        {
            var accessError = !registered ? $"Error: Tool '{name}' is unavailable in the current configuration."
                : configuration.IsComplete() ? configuration.CompletionError : "";
            if (accessError.Length == 0 && (requiresApproval || name is "bash" or "eval"))
                accessError = await approval.CheckAsync(call, requiresApproval, getEval(),
                    update => call = update, Publish, cancellationToken);
            if (accessError.Length == 0 && call.Name is "load_skill" or "read_skill_resource")
            {
                accessError = await WorkspaceFiles.ExecuteAsync(() =>
                {
                    // MAF validates paths below its discovery root; validate that root as well.
                    validateSkillRoots();
                    return Task.FromResult("");
                });
            }
            if (accessError.Length == 0 && call.Name is "read_file" or "list_directory" or "grep")
            {
                using var arguments = JsonDocument.Parse(call.Arguments);
                var path = arguments.RootElement.TryGetProperty("path", out var value)
                    ? value.GetString()! : ".";
                accessError = await WorkspaceFiles.ExecuteAsync(async () =>
                {
                    await configuration.Files.AuthorizeReadAsync(path, call.Name, configuration.PermissionCallId ?? call.CallId, cancellationToken);
                    return "";
                });
            }
            if (accessError.Length == 0 && name == "generate_image")
            {
                accessError = await WorkspaceFiles.ExecuteAsync(async () =>
                {
                    var imageGeneration = getImageGeneration();
                    if (imageGeneration?.Provider is null || imageGeneration.Error is not null)
                        return "Error: " + (imageGeneration?.Error ?? "Генерация изображений не настроена.");
                    using var arguments = JsonDocument.Parse(argumentsJson);
                    var root = arguments.RootElement;
                    var imageFiles = configuration.Files;
                    imageFiles.ValidateNewImagePath(root.GetProperty("output_path").GetString()!);
                    if (root.TryGetProperty("input_paths", out var paths) && paths.ValueKind != JsonValueKind.Null)
                    {
                        if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() > Attachments.ImageAttachments.MaxAttachments)
                            return "Error: Invalid reference image list.";
                        foreach (var path in paths.EnumerateArray())
                            await imageFiles.AuthorizeReadAsync(path.GetString()!, name, callId, cancellationToken);
                    }
                    return "";
                });
            }
            var result = accessError.Length == 0 ? await invoke(cancellationToken) : accessError;
            if (name == "delegate") delegation = getDelegation()?.SnapshotForInvocation(callId);
            var text = result switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } value => value.GetString()!,
                _ => JsonSerializer.Serialize(result)
            };
            Publish(call with
            {
                Status = text.StartsWith("Error:", StringComparison.Ordinal) || delegation?.Status == DelegationStatus.Failed
                    ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                Result = text,
                FileChange = fileChange,
                Delegation = delegation,
                BashExecution = bashExecution
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            Publish(call with
            {
                Status = ToolCallStatus.Cancelled, Result = "Call cancelled.", BashExecution = bashExecution, Delegation = delegation
            });
            throw;
        }
        catch (Exception error)
        {
            Publish(call with
            {
                Status = ToolCallStatus.Failed, Result = error.Message, BashExecution = bashExecution, Delegation = delegation
            });
            throw;
        }
        finally
        {
            _delegationInvocation.Value = previousInvocation;
            _fileChangeObserver.Value = previousObserver;
            _bashOutputObserver.Value = previousBashObserver;
        }
    }

}
