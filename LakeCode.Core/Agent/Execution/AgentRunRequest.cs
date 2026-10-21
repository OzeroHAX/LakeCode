using LakeCode.Core.Providers;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Execution;

/// <summary>Accepted settings for a turn; never persisted with provider credentials.</summary>
internal sealed record AgentRunRequest(
    ChatMessage Message,
    ModelInfo Model,
    string ReasoningEffort,
    ProviderProfile Provider,
    Settings.ModelConfiguration? SubagentConfiguration = null,
    AgentMode Mode = AgentMode.Build,
    Settings.ImageGenerationConfiguration? ImageGeneration = null);
