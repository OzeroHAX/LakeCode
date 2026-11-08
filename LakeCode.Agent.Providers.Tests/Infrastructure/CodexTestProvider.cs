using LakeCode.Agent.Providers.Custom;

namespace LakeCode.Agent.Providers.Tests;

/// <summary>Builds a real CodexProvider wired to the secrets file and to a real network transport.</summary>
internal static class CodexTestProvider
{
    internal static CodexProvider Create(CodexSecrets secrets) =>
        new(new CodexSecretsConfigSource(secrets), handler: null);

    internal static IStreamingAgentModel Streaming(CodexProvider provider, string modelId) =>
        (IStreamingAgentModel)provider.GetModel(modelId);
}
