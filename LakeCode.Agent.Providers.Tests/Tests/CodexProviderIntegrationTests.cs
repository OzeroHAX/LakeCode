using System.Text.Json;
using LakeCode.Agent;
using LakeCode.Agent.Providers.Custom;

namespace LakeCode.Agent.Providers.Tests;

/// <summary>
/// Hits the real Codex backend at chatgpt.com. Credentials come from the git-ignored
/// Secrets/codex.json that is copied into the build output; without it the tests are skipped.
/// </summary>
public sealed class CodexProviderIntegrationTests
{
    private const string TextMarker = "PINEAPPLE";
    private const string ToolName = "get_test_weather";
    private const string ToolArgument = "city";
    private const string ToolCity = "Lisbon";

    [CodexIntegration]
    public async Task GetModelsAsync_LoadsCatalog()
    {
        var secrets = CodexSecrets.Require();
        var provider = CodexTestProvider.Create(secrets);
        using var cancellation = new CancellationTokenSource(secrets.Timeout);

        var models = await provider.GetModelsAsync(cancellation.Token);

        Assert.NotEmpty(models);
        Assert.All(models, model =>
        {
            Assert.Equal("openai-codex", model.ProviderId);
            Assert.False(string.IsNullOrWhiteSpace(model.Id));
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
        });
        Assert.Equal(
            models.Count,
            models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [CodexIntegration]
    public async Task RespondAsync_ReturnsText()
    {
        var secrets = CodexSecrets.Require();
        var model = CodexTestProvider.Create(secrets).GetModel(secrets.Model);
        using var cancellation = new CancellationTokenSource(secrets.Timeout);

        var response = await model.RespondAsync([TextRequest()], [], cancellation.Token);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Contains(TextMarker, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(response.ToolCalls);
    }

    [CodexIntegration]
    public async Task RespondAsync_ReturnsToolCall()
    {
        var secrets = CodexSecrets.Require();
        var model = CodexTestProvider.Create(secrets).GetModel(secrets.Model);
        using var cancellation = new CancellationTokenSource(secrets.Timeout);

        var response = await model.RespondAsync(
            [ToolRequest()],
            [WeatherTool()],
            cancellation.Token);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(ToolName, call.ToolName);
        Assert.False(string.IsNullOrWhiteSpace(call.ToolCallId));
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Equal(ToolCity, call.Arguments.GetProperty(ToolArgument).GetString());
    }

    [CodexIntegration]
    public async Task RespondStreamingAsync_ReturnsTextDeltas()
    {
        var secrets = CodexSecrets.Require();
        var streaming = CodexTestProvider.Streaming(CodexTestProvider.Create(secrets), secrets.Model);
        using var cancellation = new CancellationTokenSource(secrets.Timeout);

        var updates = await CollectAsync(streaming, [TextRequest()], [], cancellation.Token);

        var text = string.Concat(updates.OfType<AgentTextDelta>().Select(delta => delta.Text));
        Assert.False(string.IsNullOrEmpty(text));
        Assert.Contains(TextMarker, text, StringComparison.OrdinalIgnoreCase);
    }

    [CodexIntegration]
    public async Task RespondStreamingAsync_ReturnsToolCall()
    {
        var secrets = CodexSecrets.Require();
        var streaming = CodexTestProvider.Streaming(CodexTestProvider.Create(secrets), secrets.Model);
        using var cancellation = new CancellationTokenSource(secrets.Timeout);

        var updates = await CollectAsync(streaming, [ToolRequest()], [WeatherTool()], cancellation.Token);

        var started = Assert.Single(updates.OfType<AgentToolCallStarted>());
        var completed = Assert.Single(updates.OfType<AgentToolCallCompleted>());
        Assert.Equal(ToolName, started.ToolName);
        Assert.Equal(started.ToolCallId, completed.ToolCall.ToolCallId);
        Assert.Equal(ToolName, completed.ToolCall.ToolName);
        Assert.Equal(ToolCity, completed.ToolCall.Arguments.GetProperty(ToolArgument).GetString());
        Assert.All(
            updates.OfType<AgentToolCallDelta>(),
            delta => Assert.Equal(started.ToolCallId, delta.ToolCallId));
    }

    [CodexIntegration]
    public async Task RespondStreamingAsync_ReturnsReasoning()
    {
        var secrets = CodexSecrets.Require();
        var streaming = CodexTestProvider.Streaming(CodexTestProvider.Create(secrets), secrets.Model);
        using var cancellation = new CancellationTokenSource(secrets.Timeout);

        var updates = await CollectAsync(
            streaming,
            [new UserMessage("reasoning-1", "Think step by step about 17 * 23, then answer with just the result.")],
            [],
            cancellation.Token);

        var thinking = updates.OfType<AgentThinkingDelta>().ToArray();
        Assert.NotEmpty(thinking);
        Assert.All(thinking, delta => Assert.False(string.IsNullOrEmpty(delta.Thinking)));
    }

    private static UserMessage TextRequest() =>
        new("text-1", $"Reply with exactly the word {TextMarker} and nothing else.");

    private static UserMessage ToolRequest() =>
        new("tool-1", $"Call the {ToolName} tool for city '{ToolCity}'. Do not answer with plain text.");

    private static AgentToolDefinition WeatherTool()
    {
        using var schema = JsonDocument.Parse(
            $$"""
            {
              "type": "object",
              "properties": { "{{ToolArgument}}": { "type": "string" } },
              "required": ["{{ToolArgument}}"],
              "additionalProperties": false
            }
            """);

        return new AgentToolDefinition(
            ToolName,
            "Returns the current weather for a city.",
            schema.RootElement.Clone());
    }

    private static async Task<List<AgentModelUpdate>> CollectAsync(
        IStreamingAgentModel streaming,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var updates = new List<AgentModelUpdate>();
        await foreach (var update in streaming.RespondStreamingAsync(messages, tools, cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }
}
