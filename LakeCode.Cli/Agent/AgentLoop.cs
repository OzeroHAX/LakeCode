using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using LakeCode.Cli.Agent.Tools;
using LakeCode.Cli.Agent.Tools.ReadFile;
using LakeCode.Cli.Agent.Tools.ListDirectory;
using LakeCode.Cli.Agent.Tools.Grep;
using LakeCode.Cli.Agent.Tools.Bash;
using LakeCode.Cli.Agent.Tools.WriteFile;
using LakeCode.Cli.Agent.Tools.EditFile;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace LakeCode.Cli.Agent;

/// <summary>A single conversation; consume each response before starting the next turn. Model and reasoning apply per request.</summary>
public sealed class AgentLoop
{
    private readonly AIAgent _agent;
    private readonly string _cacheSessionId = Guid.NewGuid().ToString("D");
    private AgentSession? _session;

    public event Action<ToolCallUpdate>? ToolCallUpdated;

    public AgentLoop()
    {
        var apiKey = Environment.GetEnvironmentVariable("WIKIVIBE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Set WIKIVIBE_API_KEY before creating AgentLoop.");
        }

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri("https://api.wikivibe.ru/v1") };
        clientOptions.AddPolicy(new CodexSessionHeadersPolicy(_cacheSessionId), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

        var files = new WorkspaceFiles();
        var promptContext = new SystemPromptContext(
            files.Root,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString());
        var readFile = new ReadFileTool(files);
        var listDirectory = new ListDirectoryTool(files);
        var grep = new GrepTool(files);
        var bash = new BashTool(files);
        var writeFile = new WriteFileTool(files);
        var editFile = new EditFileTool(files);

        // The OpenAI SDK requires explicit opt-in for its Responses API.
#pragma warning disable OPENAI001
        _agent = client.GetResponsesClient().AsAIAgent(
            model: "gpt-6.1-sol",
            name: "LakeCode",
            instructions: SystemPromptBuilder.Build(promptContext),
            tools:
            [
                AIFunctionFactory.Create(readFile.ReadAsync, name: "read_file"),
                AIFunctionFactory.Create(listDirectory.ListAsync, name: "list_directory"),
                AIFunctionFactory.Create(grep.SearchAsync, name: "grep"),
                AIFunctionFactory.Create(bash.RunAsync, name: "bash"),
                AIFunctionFactory.Create(writeFile.WriteAsync, name: "write_file"),
                AIFunctionFactory.Create(editFile.EditAsync, name: "edit_file")
            ]).AsBuilder().Use(ObserveToolCallAsync).Build();
        #pragma warning restore OPENAI001
    }

    private async ValueTask<object?> ObserveToolCallAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var call = new ToolCallUpdate(context.CallContent.CallId, context.Function.Name,
            JsonSerializer.Serialize(context.Arguments), ToolCallStatus.Running);
        ToolCallUpdated?.Invoke(call);
        try
        {
            var result = await next(context, cancellationToken);
            var text = result as string ?? JsonSerializer.Serialize(result);
            ToolCallUpdated?.Invoke(call with
            {
                Status = text.StartsWith("Error:", StringComparison.Ordinal)
                    ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                Result = text
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            ToolCallUpdated?.Invoke(call with { Status = ToolCallStatus.Cancelled, Result = "Call cancelled." });
            throw;
        }
        catch (Exception error)
        {
            ToolCallUpdated?.Invoke(call with { Status = ToolCallStatus.Failed, Result = error.Message });
            throw;
        }
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RunAsync(
        string message,
        ModelInfo model,
        string reasoningEffort,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(model);
        if (!model.ReasoningEfforts.Contains(reasoningEffort))
            throw new ArgumentException("The model does not support this reasoning effort.", nameof(reasoningEffort));
        _session ??= await _agent.CreateSessionAsync(cancellationToken: cancellationToken);

#pragma warning disable OPENAI001
        var options = new ChatClientAgentRunOptions(new ChatOptions
        {
            ModelId = model.Id,
            RawRepresentationFactory = _ =>
            {
                var responseOptions = new CreateResponseOptions
                {
                    ReasoningOptions = new ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = new ResponseReasoningEffortLevel(reasoningEffort),
                        ReasoningSummaryVerbosity = reasoningEffort == "none"
                            ? null
                            : ResponseReasoningSummaryVerbosity.Auto
                    }
                };
                // The SDK exposes unmapped Responses fields through its JSON patch.
#pragma warning disable SCME0001
                responseOptions.Patch.Set("$.prompt_cache_key"u8, _cacheSessionId);
#pragma warning restore SCME0001
                return responseOptions;
            }
        });
#pragma warning restore OPENAI001

        (string ItemId, int SummaryIndex)? previousSummary = null;
        await foreach (var update in _agent.RunStreamingAsync(
                           message, _session, options: options, cancellationToken: cancellationToken))
        {
#pragma warning disable OPENAI001
            if (update.AsChatResponseUpdate().RawRepresentation is StreamingResponseReasoningSummaryTextDeltaUpdate delta)
            {
                var summary = (delta.ItemId, delta.SummaryIndex);
                if (previousSummary is { } previous && previous != summary)
                {
                    // Summary parts are independent Markdown; token deltas within a part are not.
                    foreach (var content in update.Contents.OfType<TextReasoningContent>())
                        content.Text = "\n\n" + content.Text;
                }
                previousSummary = summary;
            }
#pragma warning restore OPENAI001
            yield return update;
        }
    }
}
