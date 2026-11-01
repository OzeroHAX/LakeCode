using System.ClientModel.Primitives;

namespace LakeCode.Core.Providers;

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
