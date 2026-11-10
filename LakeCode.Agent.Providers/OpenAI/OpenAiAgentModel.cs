using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace LakeCode.Agent.Providers.OpenAI;

public sealed class OpenAiAgentModel : IAgentModel, IStreamingAgentModel
{
    private readonly ChatClient _client;

    public OpenAiAgentModel(
        ChatClient client,
        string providerId,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _client = client;
        ProviderId = providerId;
        ModelId = modelId;
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public async ValueTask<AgentModelResponse> RespondAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        ChatCompletion completion = await _client.CompleteChatAsync(
                CreateMessages(messages),
                CreateOptions(tools),
                cancellationToken)
            .ConfigureAwait(false);

        return CreateResponse(completion);
    }

    public async IAsyncEnumerable<AgentModelUpdate> RespondStreamingAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var toolCalls = new OpenAiToolCallAccumulator();
        var startedToolCalls = new HashSet<int>();
        var updates = _client.CompleteChatStreamingAsync(
            CreateMessages(messages),
            CreateOptions(tools),
            cancellationToken);

        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var contentPart in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(contentPart.Text))
                {
                    yield return new AgentTextDelta(contentPart.Text);
                }
            }

            foreach (var toolCallUpdate in update.ToolCallUpdates)
            {
                OpenAiToolCallAccumulator.Call call = toolCalls.Append(toolCallUpdate);

                if (call.Id is not null && call.Name is not null && startedToolCalls.Add(toolCallUpdate.Index))
                {
                    yield return new AgentToolCallStarted(call.Id, call.Name);
                }

                if (call.Id is not null && toolCallUpdate.FunctionArgumentsUpdate is not null)
                {
                    ReadOnlyMemory<byte> argumentsUpdate =
                        toolCallUpdate.FunctionArgumentsUpdate.ToMemory();
                    string argumentsDelta = call.AppendArguments(argumentsUpdate);

                    if (argumentsDelta.Length > 0)
                    {
                        yield return new AgentToolCallDelta(
                            call.Id,
                            argumentsDelta);
                    }
                }
            }
        }

        foreach (AgentToolCall toolCall in toolCalls.Build())
        {
            yield return new AgentToolCallCompleted(toolCall);
        }
    }

    private static List<ChatMessage> CreateMessages(IReadOnlyList<AgentMessage> messages)
    {
        List<ChatMessage> result = [];
        var pendingImages = new List<ChatMessageContentPart>();

        void FlushImages()
        {
            if (pendingImages.Count == 0) return;
            result.Add(ChatMessage.CreateUserMessage(pendingImages));
            pendingImages = [];
        }

        foreach (AgentMessage message in messages)
        {
            if (message is not ToolResultMessage) FlushImages();
            switch (message)
            {
                case UserMessage userMessage:
                    result.Add(ChatMessage.CreateUserMessage(userMessage.Text));
                    break;

                case SystemMessage systemMessage:
                    result.Add(ChatMessage.CreateSystemMessage(systemMessage.Text));
                    break;

                case AssistantMessage assistantMessage:
                    result.Add(CreateAssistantMessage(assistantMessage));
                    break;

                case ToolResultMessage toolResultMessage:
                    result.Add(ChatMessage.CreateToolMessage(
                        toolResultMessage.ToolCallId,
                        toolResultMessage.Result));

                    // Tool messages accept text only, so the images follow as a separate user message.
                    if (toolResultMessage.Images is { Count: > 0 } images)
                    {
                        pendingImages.Add(ChatMessageContentPart.CreateTextPart(
                                $"Images returned by tool '{toolResultMessage.ToolName}'."));

                        foreach (AgentImage image in images)
                        {
                            pendingImages.Add(ChatMessageContentPart.CreateImagePart(
                                BinaryData.FromBytes(image.Data),
                                image.MediaType,
                                ChatImageDetailLevel.Auto));
                        }

                        // Flush only after the complete contiguous batch of tool outputs.
                    }

                    break;

                default:
                    throw new NotSupportedException(
                        $"Agent message type '{message.GetType().Name}' is not supported by OpenAI Chat.");
            }
        }

        FlushImages();
        return result;
    }

    private static AssistantChatMessage CreateAssistantMessage(AssistantMessage message)
    {
        if (message.ToolCalls.Count == 0)
        {
            return ChatMessage.CreateAssistantMessage(message.Text);
        }

        var toolCalls = message.ToolCalls
            .Select(toolCall => ChatToolCall.CreateFunctionToolCall(
                toolCall.ToolCallId,
                toolCall.ToolName,
                BinaryData.FromString(toolCall.Arguments.GetRawText())))
            .ToArray();
        AssistantChatMessage assistantMessage = ChatMessage.CreateAssistantMessage(toolCalls);

        if (message.Text.Length > 0)
        {
            assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(message.Text));
        }

        return assistantMessage;
    }

    private static ChatCompletionOptions CreateOptions(
        IReadOnlyList<AgentToolDefinition> tools)
    {
        var options = new ChatCompletionOptions();

        foreach (AgentToolDefinition tool in tools)
        {
            BinaryData? parameters = tool.ParametersSchema is JsonElement schema
                ? BinaryData.FromString(schema.GetRawText())
                : null;

            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                parameters));
        }

        return options;
    }

    private static AgentModelResponse CreateResponse(ChatCompletion completion)
    {
        string text = string.Concat(completion.Content.Select(contentPart => contentPart.Text));
        var toolCalls = completion.ToolCalls
            .Select(toolCall => new AgentToolCall(
                toolCall.Id,
                toolCall.FunctionName,
                ParseArguments(toolCall.FunctionArguments.ToString())))
            .ToArray();

        return new AgentModelResponse(text, toolCalls);
    }

    private static JsonElement ParseArguments(string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        return document.RootElement.Clone();
    }

    private sealed class OpenAiToolCallAccumulator
    {
        private readonly SortedDictionary<int, Call> _calls = [];

        public Call Append(StreamingChatToolCallUpdate update)
        {
            if (!_calls.TryGetValue(update.Index, out Call? call))
            {
                call = new Call();
                _calls.Add(update.Index, call);
            }

            if (update.ToolCallId is not null)
            {
                call.Id = update.ToolCallId;
            }

            if (update.FunctionName is not null)
            {
                call.Name = update.FunctionName;
            }

            return call;
        }

        public IEnumerable<AgentToolCall> Build()
        {
            foreach (Call call in _calls.Values)
            {
                if (call.Id is null || call.Name is null)
                {
                    throw new InvalidOperationException(
                        "OpenAI returned an incomplete tool call.");
                }

                yield return new AgentToolCall(
                    call.Id,
                    call.Name,
                    ParseArguments(Encoding.UTF8.GetString(call.Arguments.ToArray())));
            }
        }

        public sealed class Call
        {
            private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

            public string? Id { get; set; }

            public string? Name { get; set; }

            public MemoryStream Arguments { get; } = new();

            public string AppendArguments(ReadOnlyMemory<byte> arguments)
            {
                Arguments.Write(arguments.Span);

                char[] buffer = new char[Encoding.UTF8.GetMaxCharCount(arguments.Length)];
                int charCount = _decoder.GetChars(arguments.Span, buffer, flush: false);
                return new string(buffer, 0, charCount);
            }
        }
    }
}
