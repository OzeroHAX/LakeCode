using System.ClientModel.Primitives;

namespace LakeCode.Core.Providers.Custom;

internal sealed record OpenCodeProvider(ModelsDevProvider Catalog, string Endpoint, string? ModelsEndpoint)
    : OpenAICompatibleProvider(Catalog.Id, Catalog.Name, OpenAIApiMode.ChatCompletions, Endpoint, Catalog.Id == "opencode-go"
        ? "https://opencode.ai/inference/go/v1/models" : "https://opencode.ai/inference/v1/models", Catalog.Id,
        Catalog.EnvironmentVariables, Catalog.DocumentationUrl, Catalog.Models.Count)
{
    internal override void ConfigureOpenAI(OpenAI.OpenAIClientOptions options, ProviderProfile profile, string sessionId) =>
        options.AddPolicy(new OpenCodeSessionHeadersPolicy(sessionId), System.ClientModel.Primitives.PipelinePosition.PerCall);
}

/// <summary>OpenCode routes and caches requests per stable conversation session.</summary>
internal sealed class OpenCodeSessionHeadersPolicy(string sessionId) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        SetHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        SetHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void SetHeaders(PipelineMessage message) => message.Request.Headers.Set("x-opencode-session", sessionId);
}
