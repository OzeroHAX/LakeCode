using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LakeCode.Agent;
using OpenAI.Responses;

namespace LakeCode.Agent.Providers.Custom;

#pragma warning disable OPENAI001

internal sealed class CodexAgentModel(
    ResponsesClient client,
    string providerId,
    string modelId) : IAgentModel, IStreamingAgentModel
{
    public string ProviderId => providerId;

    public string ModelId => modelId;

    public async ValueTask<AgentModelResponse> RespondAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        List<ResponseItem> input = CreateInput(messages);
        CreateResponseOptions options = CreateOptions(input, tools, streaming: false);
        ResponseResult response = await client
            .CreateResponseAsync(options, cancellationToken)
            .ConfigureAwait(false);

        return CreateResponse(response);
    }

    public async IAsyncEnumerable<AgentModelUpdate> RespondStreamingAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CreateResponseOptions options = CreateOptions(CreateInput(messages), tools, streaming: true);
        AsyncStreamingResult<StreamingResponseUpdate> updates =
            await client.CreateResponseStreamingAsync(options, cancellationToken)
                .ConfigureAwait(false);

        Dictionary<string, string> toolCallIds = new(StringComparer.Ordinal);

        await foreach (StreamingResponseUpdate update in updates
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (update)
            {
                case StreamingResponseOutputTextDeltaUpdate text:
                    if (!string.IsNullOrEmpty(text.Delta))
                    {
                        yield return new AgentTextDelta(text.Delta);
                    }
                    break;

                case StreamingResponseReasoningTextDeltaUpdate reasoning:
                    if (!string.IsNullOrEmpty(reasoning.Delta))
                    {
                        yield return new AgentThinkingDelta(reasoning.Delta);
                    }
                    break;

                case StreamingResponseReasoningSummaryTextDeltaUpdate reasoningSummary:
                    if (!string.IsNullOrEmpty(reasoningSummary.Delta))
                    {
                        yield return new AgentThinkingDelta(reasoningSummary.Delta);
                    }
                    break;

                case StreamingResponseOutputItemAddedUpdate added
                    when added.Item is FunctionCallResponseItem functionCall:
                    toolCallIds[functionCall.Id] = functionCall.CallId;
                    yield return new AgentToolCallStarted(
                        functionCall.CallId,
                        functionCall.FunctionName);
                    break;

                case StreamingResponseFunctionCallArgumentsDeltaUpdate arguments:
                    if (!string.IsNullOrEmpty(arguments.Delta.ToString()) &&
                        toolCallIds.TryGetValue(arguments.ItemId, out string? toolCallId))
                    {
                        yield return new AgentToolCallDelta(
                            toolCallId,
                            arguments.Delta.ToString());
                    }
                    break;

                case StreamingResponseOutputItemDoneUpdate done
                    when done.Item is FunctionCallResponseItem functionCall:
                    yield return new AgentToolCallCompleted(
                        new AgentToolCall(
                            functionCall.CallId,
                            functionCall.FunctionName,
                            ParseArguments(functionCall.FunctionArguments.ToString())));
                    break;
            }
        }
    }

    private CreateResponseOptions CreateOptions(
        List<ResponseItem> input,
        IReadOnlyList<AgentToolDefinition> tools,
        bool streaming)
    {
        CreateResponseOptions options = new(modelId, input)
        {
            StreamingEnabled = streaming,
            StoredOutputEnabled = false,
        };

        foreach (AgentToolDefinition tool in tools)
        {
            options.Tools.Add(ResponseTool.CreateFunctionTool(
                functionName: tool.Name,
                functionParameters: tool.ParametersSchema is JsonElement schema
                    ? BinaryData.FromString(schema.GetRawText())
                    : null,
                strictModeEnabled: false,
                functionDescription: tool.Description));
        }

        return options;
    }

    private static List<ResponseItem> CreateInput(
        IReadOnlyList<AgentMessage> messages)
    {
        List<ResponseItem> input = [];

        foreach (AgentMessage message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    input.Add(ResponseItem.CreateUserMessageItem(user.Text));
                    break;

                case SystemMessage system:
                    input.Add(ResponseItem.CreateSystemMessageItem(system.Text));
                    break;

                case AssistantMessage assistant:
                    if (!string.IsNullOrEmpty(assistant.Text))
                    {
                        input.Add(ResponseItem.CreateAssistantMessageItem(assistant.Text));
                    }

                    foreach (AgentToolCall call in assistant.ToolCalls)
                    {
                        input.Add(new FunctionCallResponseItem(
                            call.ToolCallId,
                            call.ToolName,
                            BinaryData.FromString(call.Arguments.GetRawText())));
                    }
                    break;

                case ToolResultMessage toolResult:
                    input.Add(new FunctionCallOutputResponseItem(
                        toolResult.ToolCallId,
                        toolResult.Result));
                    break;

                default:
                    throw new NotSupportedException(
                        $"Agent message type '{message.GetType().Name}' is not supported by Codex.");
            }
        }

        return input;
    }

    private static AgentModelResponse CreateResponse(ResponseResult response)
    {
        string text = response.GetOutputText();
        List<AgentToolCall> toolCalls = [];

        foreach (ResponseItem item in response.OutputItems)
        {
            if (item is FunctionCallResponseItem functionCall)
            {
                toolCalls.Add(new AgentToolCall(
                    functionCall.CallId,
                    functionCall.FunctionName,
                    ParseArguments(functionCall.FunctionArguments.ToString())));
            }
        }

        return new AgentModelResponse(text, toolCalls);
    }

    private static JsonElement ParseArguments(string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        return document.RootElement.Clone();
    }
}

#pragma warning restore OPENAI001
