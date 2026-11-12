namespace LakeCode.Core.Providers.Custom;

internal sealed record FreeLLMProvider() : OpenAICompatibleProvider(
    "freellm", "FreeLLM", OpenAIApiMode.ChatCompletions,
    "http://127.0.0.1:3001/v1", null, "", [], "http://127.0.0.1:3001/docs", 0);