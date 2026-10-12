using System.ClientModel.Primitives;

namespace LakeCode.Cli.Agent;

/// <summary>Keeps Codex cache routing stable for all requests made by one agent session.</summary>
internal sealed class CodexSessionHeadersPolicy(string sessionId) : PipelinePolicy
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

    private void SetHeaders(PipelineMessage message)
    {
        message.Request.Headers.Set("session-id", sessionId);
        message.Request.Headers.Set("thread-id", sessionId);
    }
}
