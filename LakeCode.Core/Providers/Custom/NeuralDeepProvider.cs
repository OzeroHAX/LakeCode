namespace LakeCode.Core.Providers.Custom;

internal sealed record NeuralDeepProvider() : OpenAICompatibleProvider(
    "neuraldeep", "NeuralDeep", OpenAIApiMode.ChatCompletions,
    "https://api.neuraldeep.ru/v1", null, "", [], "https://neuraldeep.ru/docs", 0);
