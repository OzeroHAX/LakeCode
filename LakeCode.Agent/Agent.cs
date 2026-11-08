using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace LakeCode.Agent;

public sealed class Agent
{
    private readonly List<AgentEvent> _events = [];
    private readonly List<AgentMessage> _messages = [];
    private readonly IAgentModel _model;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private ChannelWriter<AgentEvent>? _streamWriter;
    private int _runStarted;

    public Agent(IAgentModel model, IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);

        _model = model;
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }

    public AgentStatus Status { get; internal set; } = AgentStatus.Idle;

    public IReadOnlyList<AgentEvent> Events => _events;

    public event Action<AgentEvent>? EventOccurred;

    public async Task RunAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        EnsureCanRun();

        Publish(new AgentStarted(CreateId()));

        try
        {
            AppendMessage(new UserMessage(CreateId(), prompt));

            while (true)
            {
                var hasToolCalls = await RunTurnAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!hasToolCalls)
                {
                    Publish(new AgentCompleted());
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(new AgentCanceled());
            throw;
        }
        catch (Exception exception)
        {
            Publish(new AgentFailed(exception.Message));
            throw;
        }
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamingAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (_model is not IStreamingAgentModel)
        {
            throw new NotSupportedException(
                "The configured model does not support streaming responses.");
        }

        EnsureCanRun();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var runTask = ExecuteStreamingRunAsync(
            prompt,
            linkedCancellation.Token,
            channel.Writer);

        try
        {
            await foreach (var agentEvent in channel.Reader.ReadAllAsync(linkedCancellation.Token))
            {
                yield return agentEvent;
            }

            await runTask.ConfigureAwait(false);
        }
        finally
        {
            linkedCancellation.Cancel();

            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ExecuteStreamingRunAsync(
        string prompt,
        CancellationToken cancellationToken,
        ChannelWriter<AgentEvent> streamWriter)
    {
        _streamWriter = streamWriter;

        try
        {
            Publish(new AgentStarted(CreateId()));
            AppendMessage(new UserMessage(CreateId(), prompt));

            while (true)
            {
                var hasToolCalls = await RunStreamingTurnAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!hasToolCalls)
                {
                    Publish(new AgentCompleted());
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(new AgentCanceled());
            throw;
        }
        catch (Exception exception)
        {
            Publish(new AgentFailed(exception.Message));
            throw;
        }
        finally
        {
            _streamWriter = null;
            streamWriter.TryComplete();
        }
    }

    private async Task<bool> RunTurnAsync(CancellationToken cancellationToken)
    {
        var turnId = CreateId();
        Publish(new TurnStarted(turnId));

        var responseId = CreateId();
        Publish(new ModelResponseStarted(responseId));

        var response = await GetResponseAsync(responseId, cancellationToken)
            .ConfigureAwait(false);
        var assistantMessage = AppendMessage(
            new AssistantMessage(CreateId(), response.Text, response.ToolCalls),
            responseId);

        foreach (var toolCall in assistantMessage.ToolCalls)
        {
            await ExecuteToolAsync(toolCall, cancellationToken)
                .ConfigureAwait(false);
        }

        Publish(new TurnCompleted(turnId));
        return assistantMessage.ToolCalls.Count > 0;
    }

    private async Task<bool> RunStreamingTurnAsync(CancellationToken cancellationToken)
    {
        var streamingModel = (IStreamingAgentModel)_model;
        var turnId = CreateId();
        var responseId = CreateId();
        var messageId = CreateId();
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var toolCalls = new List<AgentToolCall>();
        var startedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var textStarted = false;
        var thinkingStarted = false;

        Publish(new TurnStarted(turnId));
        Publish(new ModelResponseStarted(responseId));
        Publish(new MessageStarted(messageId, AgentMessageRole.Assistant));

        try
        {
            await foreach (var update in streamingModel.RespondStreamingAsync(
                               _messages,
                               CreateToolDefinitions(),
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
                            Publish(new TextStarted(messageId));
                        }

                        text.Append(textDelta.Text);
                        Publish(new TextDelta(messageId, textDelta.Text));
                        break;

                    case AgentThinkingDelta thinkingDelta:
                        if (!thinkingStarted)
                        {
                            thinkingStarted = true;
                            Publish(new ThinkingStarted(messageId));
                        }

                        thinking.Append(thinkingDelta.Thinking);
                        Publish(new ThinkingDelta(messageId, thinkingDelta.Thinking));
                        break;

                    case AgentToolCallStarted toolCallStarted:
                        startedToolCalls.Add(toolCallStarted.ToolCallId);
                        Publish(new ToolCallStarted(
                            messageId,
                            toolCallStarted.ToolCallId,
                            toolCallStarted.ToolName));
                        break;

                    case AgentToolCallDelta toolCallDelta:
                        Publish(new ToolCallDelta(
                            messageId,
                            toolCallDelta.ToolCallId,
                            toolCallDelta.ArgumentsDelta));
                        break;

                    case AgentToolCallCompleted toolCallCompleted:
                        if (startedToolCalls.Add(toolCallCompleted.ToolCall.ToolCallId))
                        {
                            Publish(new ToolCallStarted(
                                messageId,
                                toolCallCompleted.ToolCall.ToolCallId,
                                toolCallCompleted.ToolCall.ToolName));
                        }

                        toolCalls.Add(toolCallCompleted.ToolCall);
                        Publish(new ToolCallCompleted(
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
            Publish(new ModelResponseFailed(responseId, exception.Message));
            throw;
        }

        if (textStarted)
        {
            Publish(new TextCompleted(messageId, text.ToString()));
        }

        if (thinkingStarted)
        {
            Publish(new ThinkingCompleted(messageId, thinking.ToString()));
        }

        Publish(new ModelResponseCompleted(responseId));

        var assistantMessage = new AssistantMessage(
            messageId,
            text.ToString(),
            toolCalls);
        _messages.Add(assistantMessage);
        Publish(new MessageCompleted(messageId, assistantMessage.Role));

        foreach (var toolCall in toolCalls)
        {
            await ExecuteToolAsync(toolCall, cancellationToken)
                .ConfigureAwait(false);
        }

        Publish(new TurnCompleted(turnId));
        return toolCalls.Count > 0;
    }

    private async Task<AgentModelResponse> GetResponseAsync(
        string responseId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _model.RespondAsync(
                _messages,
                CreateToolDefinitions(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Publish(new ModelResponseFailed(responseId, exception.Message));
            throw;
        }
    }

    private IReadOnlyList<AgentToolDefinition> CreateToolDefinitions() =>
        _tools.Values
            .Select(tool => new AgentToolDefinition(
                tool.Name,
                tool.Description,
                tool.ParametersSchema))
            .ToArray();

    private T AppendMessage<T>(T message, string? responseId = null)
        where T : AgentMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        Publish(new MessageStarted(message.MessageId, message.Role));

        if (message is AssistantMessage assistantMessage)
        {
            if (assistantMessage.Text.Length > 0)
            {
                Publish(new TextStarted(assistantMessage.MessageId));
                Publish(new TextCompleted(assistantMessage.MessageId, assistantMessage.Text));
            }

            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                Publish(new ToolCallStarted(
                    assistantMessage.MessageId,
                    toolCall.ToolCallId,
                    toolCall.ToolName));
                Publish(new ToolCallCompleted(
                    assistantMessage.MessageId,
                    toolCall.ToolCallId,
                    toolCall.ToolName,
                    toolCall.Arguments));
            }

            if (responseId is not null)
            {
                Publish(new ModelResponseCompleted(responseId));
            }
        }

        _messages.Add(message);
        Publish(new MessageCompleted(message.MessageId, message.Role));

        return message;
    }

    private async Task ExecuteToolAsync(
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        Publish(new ToolExecutionStarted(toolCall.ToolCallId, toolCall.ToolName));

        AgentToolResult result;
        if (!_tools.TryGetValue(toolCall.ToolName, out var tool))
        {
            result = new AgentToolResult(
                true,
                $"Tool '{toolCall.ToolName}' was not found.");
        }
        else
        {
            try
            {
                var context = new AgentToolContext(
                    toolCall.ToolCallId,
                    toolCall.ToolName,
                    Publish);

                result = await tool.ExecuteAsync(
                    toolCall.Arguments,
                    context,
                    cancellationToken).ConfigureAwait(false);
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

        Publish(new ToolExecutionCompleted<string>(
            toolCall.ToolCallId,
            result.IsError,
            result.Result));

        AppendMessage(new ToolResultMessage(
            CreateId(),
            toolCall.ToolCallId,
            toolCall.ToolName,
            result.IsError,
            result.Result));
    }

    private void EnsureCanRun()
    {
        if (Interlocked.CompareExchange(ref _runStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("An Agent can only run once.");
        }
    }

    private void Publish(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        Status = agentEvent switch
        {
            AgentStarted => AgentStatus.Running,
            AgentCompleted => AgentStatus.Completed,
            AgentFailed => AgentStatus.Failed,
            AgentCanceled => AgentStatus.Canceled,
            _ => Status,
        };

        _events.Add(agentEvent);
        EventOccurred?.Invoke(agentEvent);
        _streamWriter?.TryWrite(agentEvent);
    }

    private static string CreateId() => Guid.NewGuid().ToString("N");
}

public enum AgentStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Canceled,
}
