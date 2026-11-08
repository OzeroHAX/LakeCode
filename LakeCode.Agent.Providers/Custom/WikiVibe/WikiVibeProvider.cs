using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class WikiVibeProvider : OpenAiCompatibleProvider
{
    public WikiVibeProvider()
        : base(
            "https://api.wikivibe.dev/v1",
            ProviderConfig.Load<WikiVibeConfig>().ApiKey)
    {
    }

    public override string Id => "wikivibe";

    public override string Name => "WikiVibe";
}
