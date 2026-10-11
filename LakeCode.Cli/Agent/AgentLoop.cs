using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace LakeCode.Cli.Agent;

/// <summary>A single conversation; consume each response before starting the next turn. Model and reasoning apply per request.</summary>
public sealed class AgentLoop
{
    private readonly AIAgent _agent;
    private AgentSession? _session;

    public AgentLoop()
    {
        var apiKey = Environment.GetEnvironmentVariable("WIKIVIBE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Set WIKIVIBE_API_KEY before creating AgentLoop.");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri("https://api.wikivibe.ru/v1") });

        // The OpenAI SDK requires explicit opt-in for its Responses API.
#pragma warning disable OPENAI001
        _agent = client.GetResponsesClient().AsAIAgent(
            model: "gpt-6-luna",
            name: "LakeCode",
            instructions: "You are LakeCode, a coding assistant. Be concise and precise. " +
                          "You have no tools or access to the user's files or terminal.");
        #pragma warning restore OPENAI001
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
            RawRepresentationFactory = _ => new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = new ResponseReasoningEffortLevel(reasoningEffort),
                    ReasoningSummaryVerbosity = reasoningEffort == "none"
                        ? null
                        : ResponseReasoningSummaryVerbosity.Auto
                }
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
