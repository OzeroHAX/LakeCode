using System.Collections.Frozen;

namespace LakeCode.Core.Agent;

/// <summary>
/// OpenAI model specifications checked on 2026-10-12.
/// Third-party gateways may impose different limits or restrict capabilities.
/// </summary>
public static class ModelCatalog
{
    // Sources: https://developers.openai.com/api/docs/models/{model-id}
    // Image generation through a tool is not a native output modality.
    public static FrozenDictionary<string, ModelInfo> Models { get; } =
        new Dictionary<string, ModelInfo>(StringComparer.Ordinal)
        {
            ["gpt-6-luna"] = new(
                Id: "gpt-6-luna",
                Name: "GPT-6 Luna",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["none", "low", "medium", "high", "xhigh", "max"]),
            ["gpt-6-sol"] = new(
                Id: "gpt-6-sol",
                Name: "GPT-6 Sol",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["none", "low", "medium", "high", "xhigh", "max"]),
            ["gpt-6-astra"] = new(
                Id: "gpt-6-astra",
                Name: "GPT-6 Astra",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["low", "medium", "high", "xhigh", "max"]),
            ["gpt-6.1-sol"] = new(
                Id: "gpt-6.1-sol",
                Name: "GPT-6.1 Sol",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["low", "medium", "high", "xhigh", "max"])
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
