using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Role;
using LakeCode.Core.Agent.Session;
using LakeCode.Core.Agent.Session.History;
using LakeCode.Core.Agent.Skills;
using LakeCode.Core.Agent.Tools.Execution;
using LakeCode.Core.Providers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Execution;

/// <summary>Prepares one turn. Configuration changes happen before MAF starts consuming the run.</summary>
internal sealed class AgentRunPreparer(
    AgentConfiguration initialConfiguration,
    AgentRoleFactory? roles,
    AgentSkills? skills,
    ToolInvocationRunner invocations,
    DelegationCoordinator? delegation,
    ProviderChatClient client,
    AgentHistory history,
    string cacheSessionId)
{
    private AgentMode? _previousMode;
    public AgentConfiguration Configuration { get; private set; } = initialConfiguration;
    public Settings.ImageGenerationConfiguration? ImageGeneration { get; private set; }

    /// <summary>Runs before session creation, preserving turn-reset and validation order.</summary>
    public void BeginTurn(AgentRunRequest request)
    {
        var (message, model, reasoningEffort, _, _, mode, imageGeneration) = request;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (roles is not null)
        {
            var role = roles.Get(mode);
            role.BeginTurn(_previousMode);
            Configuration = role.Configuration;
            _previousMode = mode;
        }
        Configuration.Tools.ResetEffective();
        ImageGeneration = imageGeneration;
        history.Capture.Reset();
        invocations.BeginTurn();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(model);
        bool hasImages = message.Contents.OfType<DataContent>()
            .Any(content => content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !content.Data.IsEmpty);
        if (string.IsNullOrWhiteSpace(message.Text) && !hasImages)
            throw new ArgumentException("The message must contain text or an image.", nameof(message));
        if (hasImages && !model.InputModalities.Contains(ModelModality.Image))
            throw new ArgumentException("The model does not support image input.", nameof(model));
        if (!model.ReasoningEfforts.Contains(reasoningEffort))
            throw new ArgumentException("The model does not support this reasoning effort.", nameof(reasoningEffort));
        // Recheck discovery roots before the context provider reads skills.
        if (Configuration.UseSkills) skills?.ValidateRoots();
    }

    /// <summary>Runs after session creation so checks include the restored archive.</summary>
    public PreparedAgentRun Prepare(AgentRunRequest request, AgentSessionState session)
    {
        var (message, model, reasoningEffort, provider, subagentConfiguration, _, _) = request;
        if (!model.InputModalities.Contains(ModelModality.Image) && history.Messages
            .SelectMany(item => item.Contents).OfType<DataContent>()
            .Any(content => content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("История содержит изображения. Выберите модель с image input или начните новую сессию.");
        client.Configure(provider, model, cacheSessionId);
        history.Capture.ConfigureSideContext(provider, model, reasoningEffort, cacheSessionId);
        var origin = $"{provider.Id}|{provider.Revision}|{provider.Kind}|{provider.ApiMode}|{provider.BaseUrl.TrimEnd('/')}|{model.Id}";
        if (session.HistoryOrigin != origin) history.NormalizeForConnection();
        session.HistoryOrigin = origin;
        var options = ProviderClientFactory.Options(provider, model, reasoningEffort, cacheSessionId);
        options.Instructions = Configuration.Instructions;
        options.Tools = Configuration.Tools.Functions;
        if (Configuration.AllowMultipleToolCalls is { } multiple) options.AllowMultipleToolCalls = multiple;
        delegation?.Configure(subagentConfiguration?.Model ?? model,
            subagentConfiguration?.ReasoningEffort ?? reasoningEffort, subagentConfiguration?.Provider ?? provider);
        return new PreparedAgentRun(message, new ChatClientAgentRunOptions(options));
    }
}
