using System.Collections.Frozen;

namespace LakeCode.Core.Agent;

/// <summary>
/// Documented model specifications; sources are listed next to each model family.
/// Third-party gateways may impose different limits or restrict capabilities.
/// </summary>
public static class ModelCatalog
{
    // Sources: https://developers.openai.com/api/docs/models/{model-id}
    // Image generation through a tool is not a native output modality.
    public static FrozenDictionary<string, ModelInfo> Models { get; } =
        new Dictionary<string, ModelInfo>(StringComparer.Ordinal)
        {
            // https://api-docs.deepseek.com/quick_start/pricing
            // https://api-docs.deepseek.com/guides/thinking_mode
            // Exact gateway ID; limits follow the official DeepSeek API, not every hosting provider.
            // No "none": Chat Completions currently omits effort rather than disabling thinking.
            ["deepseek/deepseek-v4.1-flash"] = new(
                Id: "deepseek/deepseek-v4.1-flash",
                Name: "DeepSeek V4.1 Flash",
                ContextWindowTokens: 1_048_576,
                MaxInputTokens: 1_048_576,
                MaxOutputTokens: 393_216,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["low", "high", "max"]),
            // https://ai.google.dev/gemini-api/docs/models/gemini-3.1-flash-lite-preview
            // https://docs.litellm.ai/blog/gemini_3_1_flash_lite_preview
            // Retired by Google; keep the exact ID for gateways that still advertise it.
            ["gemini-3.1-flash-lite-preview"] = new(
                Id: "gemini-3.1-flash-lite-preview",
                Name: "Gemini 3.1 Flash-Lite Preview",
                ContextWindowTokens: 1_048_576,
                MaxInputTokens: 1_048_576,
                MaxOutputTokens: 65_536,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["minimal", "low", "medium", "high"]),
            // https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash
            ["gemini-3.8-flash"] = new(
                Id: "gemini-3.8-flash",
                Name: "Gemini 3.8 Flash",
                ContextWindowTokens: 1_048_576,
                MaxInputTokens: 1_048_576,
                MaxOutputTokens: 65_536,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["low", "medium", "high"]),
            // https://docs.z.ai/guides/llm/glm-5.3
            ["glm-5.3"] = new(
                Id: "glm-5.3",
                Name: "GLM-5.3",
                ContextWindowTokens: 1_048_576,
                MaxInputTokens: 1_048_576,
                MaxOutputTokens: 131_072,
                InputModalities: [ModelModality.Text],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["low", "high", "max"]),
            // https://docs.z.ai/guides/llm/glm-5.3-flash
            ["glm-5.3-flash"] = new(
                Id: "glm-5.3-flash",
                Name: "GLM-5.3 Flash",
                ContextWindowTokens: 1_048_576,
                MaxInputTokens: 1_048_576,
                MaxOutputTokens: 131_072,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["low", "high", "max"]),
            // https://developers.openai.com/api/docs/models/gpt-5.6-luna
            // https://developers.openai.com/api/docs/models/gpt-5.6-sol
            // https://developers.openai.com/api/docs/models/gpt-5.6-terra
            ["gpt-5.6-luna"] = new(
                Id: "gpt-5.6-luna",
                Name: "GPT-5.6 Luna",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["none", "low", "medium", "high", "xhigh", "max"]),
            ["gpt-5.6-sol"] = new(
                Id: "gpt-5.6-sol",
                Name: "GPT-5.6 Sol",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["none", "low", "medium", "high", "xhigh", "max"]),
            ["gpt-5.6-terra"] = new(
                Id: "gpt-5.6-terra",
                Name: "GPT-5.6 Terra",
                ContextWindowTokens: 1_050_000,
                MaxInputTokens: 922_000,
                MaxOutputTokens: 128_000,
                InputModalities: [ModelModality.Text, ModelModality.Image],
                OutputModalities: [ModelModality.Text],
                ReasoningEfforts: ["none", "low", "medium", "high", "xhigh", "max"]),
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
