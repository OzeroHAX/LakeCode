using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class OpenCodeProvider : OpenAiCompatibleProvider
{
    public override string Id => "opencode";

    public override string Name => "OpenCode";

    protected override string ApiKey =>
        ProviderConfig.Load<OpenCodeConfig>().ApiKey;

    protected override string Endpoint =>
        "https://opencode.ai/inference/v1";
}
