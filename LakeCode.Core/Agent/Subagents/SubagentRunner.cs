using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Prompt;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Agent.Tools.ReadFile;
using LakeCode.Core.Agent.Tools.ListDirectory;
using LakeCode.Core.Agent.Tools.Grep;
using LakeCode.Core.Providers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Subagents;

/// <summary>Independent read-only executor; reuses session mechanics, not a user-facing role.</summary>
internal sealed class SubagentRunner : IDisposable
{
    private readonly AgentLoop _session;

    public SubagentRunner(WorkspaceFiles files, SystemPromptContext context, InstructionsReader instructions,
        AskParentTool questions, string permissionCallId)
    {
        var configuration = new AgentConfiguration(SystemPromptBuilder.Build(context, instructions), files)
        {
            AllowMultipleToolCalls = false,
            PermissionCallId = permissionCallId,
            IsComplete = () => questions.Pending is not null || questions.Failure is not null,
            CompletionError = "Error: Execution is suspended pending the parent decision."
        };
        var readFile = new ReadFileTool(files);
        var listDirectory = new ListDirectoryTool(files);
        var grep = new GrepTool(files);
        var agent = configuration.Tools;
        agent.AddTool(readFile.AgentTool());
        agent.AddTool(listDirectory.AgentTool());
        agent.AddTool(grep.AgentTool());
        agent.AddTool(questions.AgentTool());
        _session = new AgentLoop(files, configuration);
    }

    public event Action? BeforeModelRequest
    {
        add => _session.BeforeModelRequest += value;
        remove => _session.BeforeModelRequest -= value;
    }
    public event Action<ToolCallUpdate>? ToolCallUpdated
    {
        add => _session.ToolCallUpdated += value;
        remove => _session.ToolCallUpdated -= value;
    }
    public event Action<ChatResponse>? ResponseCompleted
    {
        add => _session.ResponseCompleted += value;
        remove => _session.ResponseCompleted -= value;
    }

    public IAsyncEnumerable<AgentResponseUpdate> RunAsync(ChatMessage message, ModelInfo model,
        string effort, ProviderProfile provider, CancellationToken cancellationToken) =>
        _session.RunAsync(message, model, effort, provider, cancellationToken);

    public void PreserveInterruptedHistory() => _session.PreserveInterruptedHistory();
    public void Dispose() => _session.Dispose();
}
