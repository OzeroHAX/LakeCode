namespace LakeCode.Core.Providers.Custom;

internal sealed record WikiVibeProvider() : OpenAICompatibleProvider(
    "wikivibe", "WikiVibe", OpenAIApiMode.ChatCompletions,
    "https://api.wikivibe.dev/v1", null, "", [], "https://api.wikivibe.dev/docs", 0);
