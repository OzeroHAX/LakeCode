using LakeCode.Agent;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class CodexProvider : Provider
{
    public override string Id => "openai-codex";

    public override string Name => "Codex";

    public override IAgentModel GetModel(string modelId) =>
        throw new NotSupportedException(
            "Codex model creation is not implemented yet.");
}
