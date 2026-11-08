using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class OpenCodeProvider : OpenAiCompatibleProvider
{
    public OpenCodeProvider()
        : base(
            "https://opencode.ai/inference/v1",
            ProviderConfig.Load<OpenCodeConfig>().ApiKey)
    {
    }

    public override string Id => "opencode";

    public override string Name => "OpenCode";
}
