using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class WikiVibeProvider : OpenAiCompatibleProvider
{
    public override string Id => "wikivibe";

    public override string Name => "WikiVibe";

    protected override string ApiKey =>
        ProviderConfig.Load<WikiVibeConfig>().ApiKey;

    protected override string Endpoint =>
        "https://api.wikivibe.dev/v1";
}
