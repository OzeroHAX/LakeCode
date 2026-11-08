using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class NeuralDeepProvider : OpenAiCompatibleProvider
{
    public override string Id => "neuraldeep";

    public override string Name => "NeuralDeep";

    protected override string ApiKey =>
        ProviderConfig.Load<NeuralDeepConfig>().ApiKey;

    protected override string Endpoint =>
        "https://api.neuraldeep.ru/v1";
}
