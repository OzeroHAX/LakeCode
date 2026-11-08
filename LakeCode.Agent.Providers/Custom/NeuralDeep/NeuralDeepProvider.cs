using LakeCode.Agent.Providers;
using LakeCode.Agent.Providers.Base;

namespace LakeCode.Agent.Providers.Custom;

public sealed class NeuralDeepProvider : OpenAiCompatibleProvider
{
    public NeuralDeepProvider()
        : base(
            "https://api.neuraldeep.ru/v1",
            ProviderConfig.Load<NeuralDeepConfig>().ApiKey)
    {
    }

    public override string Id => "neuraldeep";

    public override string Name => "NeuralDeep";
}
