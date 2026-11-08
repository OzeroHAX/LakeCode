namespace LakeCode.Agent;

/// <summary>
/// Executes the model/tool loop against one history. Owns no session lifecycle, persistence, queue or
/// rendering: it asks hooks for decisions, publishes events for observation, and commits messages to
/// history. Expected outcomes are reported as an <see cref="AgentRunResult"/> instead of thrown
/// exceptions, so a session can keep running after a failed or canceled run.
/// </summary>
public sealed class AgentRunner
{
    private const string InterruptedToolResult =
        "Вызов прерван; результат не получен. Побочные эффекты могли произойти. Не повторять автоматически.";

    private readonly IAgentModel _model;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly AgentHooks _hooks;
    private readonly int _maxSteps;

    public AgentRunner(
        IAgentModel model,
        IEnumerable<IAgentTool> tools,
        AgentHooks? hooks = null,
        int maxSteps = 0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);

        _model = model;
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _hooks = hooks ?? AgentHooks.None;
        _maxSteps = maxSteps;
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentHistory history,
        string prompt,
        string runId,
        Action<AgentEvent> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(publish);

        var text = new System.Text.StringBuilder();
        var turns = 0;
        var hookContext = new AgentHookContext(runId, 0, history);

        publish(new AgentStarted(runId));

        try
        {
            await RunHookAsync(_hooks.BeforeRun, hookContext, cancellationToken).ConfigureAwait(false);
            history.Add(new UserMessage(AgentIds.Create(), prompt));

            while (true)
            {
                if (_maxSteps > 0 && turns >= _maxSteps)
                {
                    publish(new AgentStopped($"Достигнут предел шагов агента ({_maxSteps})."));
                    return new AgentRunResult(AgentStatus.Stopped, text.ToString(), null, turns);
                }

                hookContext = hookContext with { Turn = turns };
                var turn = await RunTurnAsync(history, hookContext, publish, cancellationToken)
                    .ConfigureAwait(false);
                turns++;

                if (turn.Text.Length > 0)
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(turn.Text);
                }

                if (turn.ToolCalls.Count == 0)
                {
                    publish(new AgentCompleted());
                    return new AgentRunResult(AgentStatus.Completed, text.ToString(), null, turns);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteInterruptedHistory(history);
            publish(new AgentCanceled());
            return new AgentRunResult(
                AgentStatus.Canceled,
                text.ToString(),
                "Запуск отменён. Выполненные действия не отменены.",
                turns);
        }
        catch (AgentStopRequested stop)
        {
            CompleteInterruptedHistory(history);
            publish(new AgentStopped(stop.Message));
            return new AgentRunResult(AgentStatus.Stopped, text.ToString(), stop.Message, turns);
        }
        catch (Exception exception)
        {
            CompleteInterruptedHistory(history);
            publish(new AgentFailed(exception.Message));
            await ReportErrorAsync(history, runId, turns, exception).ConfigureAwait(false);
            return new AgentRunResult(AgentStatus.Failed, text.ToString(), exception.Message, turns);
        }
        finally
        {
            await RunHookAsync(_hooks.AfterRun, hookContext, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<AgentTurnResult> RunTurnAsync(
        AgentHistory history,
        AgentHookContext hookContext,
        Action<AgentEvent> publish,
        CancellationToken cancellationToken)
    {
        var turnId = AgentIds.Create();
        publish(new TurnStarted(turnId));

        var text = string.Empty;
        IReadOnlyList<AgentToolCall> toolCalls;

        if (_model is IStreamingAgentModel streamingModel)
        {
            (text, toolCalls) = await RunStreamingModelAsync(
                    streamingModel,
                    history,
                    hookContext,
                    publish,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            (text, toolCalls) = await RunModelAsync(history, hookContext, publish, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var toolCall in toolCalls)
        {
            await ExecuteToolAsync(history, hookContext, toolCall, publish, cancellationToken)
                .ConfigureAwait(false);
        }

        publish(new TurnCompleted(turnId));
        return new AgentTurnResult(text, toolCalls);
    }

    private async Task<(string Text, IReadOnlyList<AgentToolCall> ToolCalls)> RunModelAsync(
        AgentHistory history,
        AgentHookContext hookContext,
        Action<AgentEvent> publish,
        CancellationToken cancellationToken)
    {
        var responseId = AgentIds.Create();
        publish(new ModelResponseStarted(responseId));

        AgentModelResponse response;
        try
        {
            var request = await CreateRequestAsync(history, hookContext, cancellationToken)
                .ConfigureAwait(false);
            response = await _model.RespondAsync(
                    request.Messages,
                    request.Tools,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            publish(new ModelResponseFailed(responseId, exception.Message));
            throw;
        }

        await RunHookAsync(
                _hooks.AfterModelResponse,
                hookContext,
                response,
                cancellationToken)
            .ConfigureAwait(false);

        var messageId = AgentIds.Create();
        PublishMessageStart(publish, messageId, AgentMessageRole.Assistant);

        if (response.Text.Length > 0)
        {
            publish(new TextStarted(messageId));
            publish(new TextCompleted(messageId, response.Text));
        }

        foreach (var toolCall in response.ToolCalls)
        {
            publish(new ToolCallStarted(messageId, toolCall.ToolCallId, toolCall.ToolName));
            publish(new ToolCallCompleted(
                messageId,
                toolCall.ToolCallId,
                toolCall.ToolName,
                toolCall.Arguments));
        }

        publish(new ModelResponseCompleted(responseId));
        history.Add(new AssistantMessage(messageId, response.Text, response.ToolCalls));
        publish(new MessageCompleted(messageId, AgentMessageRole.Assistant));

        return (response.Text, response.ToolCalls);
    }

    private async Task<(string Text, IReadOnlyList<AgentToolCall> ToolCalls)> RunStreamingModelAsync(
        IStreamingAgentModel model,
        AgentHistory history,
        AgentHookContext hookContext,
        Action<AgentEvent> publish,
        CancellationToken cancellationToken)
    {
        var responseId = AgentIds.Create();
        var messageId = AgentIds.Create();
        var text = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        var toolCalls = new List<AgentToolCall>();
        var startedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var textStarted = false;
        var thinkingStarted = false;

        publish(new ModelResponseStarted(responseId));
        PublishMessageStart(publish, messageId, AgentMessageRole.Assistant);

        try
        {
            var request = await CreateRequestAsync(history, hookContext, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var update in model.RespondStreamingAsync(
                               request.Messages,
                               request.Tools,
                               cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
            {
                switch (update)
                {
                    case AgentTextDelta textDelta:
                        if (!textStarted)
                        {
                            textStarted = true;
                            publish(new TextStarted(messageId));
                        }

                        text.Append(textDelta.Text);
                        publish(new TextDelta(messageId, textDelta.Text));
                        break;

                    case AgentThinkingDelta thinkingDelta:
                        if (!thinkingStarted)
                        {
                            thinkingStarted = true;
                            publish(new ThinkingStarted(messageId));
                        }

                        thinking.Append(thinkingDelta.Thinking);
                        publish(new ThinkingDelta(messageId, thinkingDelta.Thinking));
                        break;

                    case AgentToolCallStarted toolCallStarted:
                        startedToolCalls.Add(toolCallStarted.ToolCallId);
                        publish(new ToolCallStarted(
                            messageId,
                            toolCallStarted.ToolCallId,
                            toolCallStarted.ToolName));
                        break;

                    case AgentToolCallDelta toolCallDelta:
                        publish(new ToolCallDelta(
                            messageId,
                            toolCallDelta.ToolCallId,
                            toolCallDelta.ArgumentsDelta));
                        break;

                    case AgentToolCallCompleted toolCallCompleted:
                        if (startedToolCalls.Add(toolCallCompleted.ToolCall.ToolCallId))
                        {
                            publish(new ToolCallStarted(
                                messageId,
                                toolCallCompleted.ToolCall.ToolCallId,
                                toolCallCompleted.ToolCall.ToolName));
                        }

                        toolCalls.Add(toolCallCompleted.ToolCall);
                        publish(new ToolCallCompleted(
                            messageId,
                            toolCallCompleted.ToolCall.ToolCallId,
                            toolCallCompleted.ToolCall.ToolName,
                            toolCallCompleted.ToolCall.Arguments));
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported model update type '{update.GetType().Name}'.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            publish(new ModelResponseFailed(responseId, exception.Message));
            throw;
        }

        if (textStarted)
        {
            publish(new TextCompleted(messageId, text.ToString()));
        }

        if (thinkingStarted)
        {
            publish(new ThinkingCompleted(messageId, thinking.ToString()));
        }

        publish(new ModelResponseCompleted(responseId));

        await RunHookAsync(
                _hooks.AfterModelResponse,
                hookContext,
                new AgentModelResponse(text.ToString(), toolCalls),
                cancellationToken)
            .ConfigureAwait(false);

        var assistantMessage = new AssistantMessage(messageId, text.ToString(), toolCalls);
        history.Add(assistantMessage);
        publish(new MessageCompleted(messageId, AgentMessageRole.Assistant));

        return (assistantMessage.Text, assistantMessage.ToolCalls);
    }

    private async Task ExecuteToolAsync(
        AgentHistory history,
        AgentHookContext hookContext,
        AgentToolCall toolCall,
        Action<AgentEvent> publish,
        CancellationToken cancellationToken)
    {
        var decision = await DecideToolAsync(hookContext, toolCall, cancellationToken)
            .ConfigureAwait(false);

        if (decision.Kind == AgentToolDecisionKind.Stop)
        {
            throw new AgentStopRequested(decision.Reason ?? $"Инструмент '{toolCall.ToolName}' остановил запуск.");
        }

        AgentToolResult result;
        if (decision.Kind == AgentToolDecisionKind.Deny)
        {
            result = new AgentToolResult(true, decision.Reason ?? "Вызов инструмента отклонён.");
        }
        else
        {
            publish(new ToolExecutionStarted(toolCall.ToolCallId, toolCall.ToolName));

            if (!_tools.TryGetValue(toolCall.ToolName, out var tool))
            {
                result = new AgentToolResult(true, $"Tool '{toolCall.ToolName}' was not found.");
            }
            else
            {
                try
                {
                    var context = new AgentToolContext(
                        toolCall.ToolCallId,
                        toolCall.ToolName,
                        publish);

                    result = await tool.ExecuteAsync(
                            toolCall.Arguments,
                            context,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result = new AgentToolResult(true, exception.Message);
                }
            }

            result = await RunAfterToolHookAsync(hookContext, toolCall, result, cancellationToken)
                .ConfigureAwait(false);
            publish(new ToolExecutionCompleted<string>(
                toolCall.ToolCallId,
                result.IsError,
                result.Result));
        }

        var toolResultMessage = new ToolResultMessage(
            AgentIds.Create(),
            toolCall.ToolCallId,
            toolCall.ToolName,
            result.IsError,
            result.Result);

        PublishMessageStart(publish, toolResultMessage.MessageId, AgentMessageRole.ToolResult);
        history.Add(toolResultMessage);
        publish(new MessageCompleted(toolResultMessage.MessageId, AgentMessageRole.ToolResult));
    }

    private async Task<AgentModelRequest> CreateRequestAsync(
        AgentHistory history,
        AgentHookContext hookContext,
        CancellationToken cancellationToken)
    {
        var request = new AgentModelRequest(history.Snapshot(), CreateToolDefinitions());

        return _hooks.BeforeModelRequest is { } hook
            ? await hook(hookContext, request, cancellationToken).ConfigureAwait(false)
            : request;
    }

    private async Task<AgentToolDecision> DecideToolAsync(
        AgentHookContext hookContext,
        AgentToolCall toolCall,
        CancellationToken cancellationToken) =>
        _hooks.BeforeToolExecution is { } hook
            ? await hook(hookContext, toolCall, cancellationToken).ConfigureAwait(false)
            : AgentToolDecision.Allow;

    private async Task<AgentToolResult> RunAfterToolHookAsync(
        AgentHookContext hookContext,
        AgentToolCall toolCall,
        AgentToolResult result,
        CancellationToken cancellationToken) =>
        _hooks.AfterToolExecution is { } hook
            ? await hook(hookContext, toolCall, result, cancellationToken).ConfigureAwait(false)
            : result;

    private async Task ReportErrorAsync(
        AgentHistory history,
        string runId,
        int turns,
        Exception exception)
    {
        if (_hooks.OnError is not { } hook)
        {
            return;
        }

        try
        {
            await hook(new AgentHookContext(runId, turns, history), exception, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The run already failed; a failing diagnostic hook must not replace the original error.
        }
    }

    private static async ValueTask RunHookAsync(
        Func<AgentHookContext, CancellationToken, ValueTask>? hook,
        AgentHookContext context,
        CancellationToken cancellationToken)
    {
        if (hook is not null)
        {
            await hook(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask RunHookAsync(
        Func<AgentHookContext, AgentModelResponse, CancellationToken, ValueTask>? hook,
        AgentHookContext context,
        AgentModelResponse response,
        CancellationToken cancellationToken)
    {
        if (hook is not null)
        {
            await hook(context, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void PublishMessageStart(
        Action<AgentEvent> publish,
        string messageId,
        AgentMessageRole role) =>
        publish(new MessageStarted(messageId, role));

    private static void CompleteInterruptedHistory(AgentHistory history) =>
        history.CompleteInterruptedToolCalls(InterruptedToolResult);

    private IReadOnlyList<AgentToolDefinition> CreateToolDefinitions() =>
        _tools.Values
            .Select(tool => new AgentToolDefinition(
                tool.Name,
                tool.Description,
                tool.ParametersSchema))
            .ToArray();

    private sealed record AgentTurnResult(string Text, IReadOnlyList<AgentToolCall> ToolCalls);
}

/// <summary>Thrown internally when a pre-tool hook stops the run; never escapes the runner.</summary>
internal sealed class AgentStopRequested(string reason) : Exception(reason);
