using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Prompt;
using LakeCode.Core.Agent.Tools.Registration;

namespace LakeCode.Core.Agent.Role.Research;

internal sealed class ResearchRole : AgentRole
{
    public ResearchRole(SystemPromptContext context, InstructionsReader instructions, AgentToolServices services)
        : base(AgentMode.Research, new AgentConfiguration(SystemPromptBuilder.Build(context, instructions), services.Files))
    {
        var agent = Configuration.Tools;
        agent.AddTool(services.ReadFile.AgentTool());
        agent.AddTool(services.ListDirectory.AgentTool());
        agent.AddTool(services.Grep.AgentTool());
        agent.AddTool(services.WriteFile.AgentTool());
        agent.AddTool(services.ImageGeneration.AgentTool());
        agent.AddTool(services.WebSearch.AgentTool());
        agent.AddTool(services.ReadWeb.AgentTool());
        agent.AddTool(services.Ask.AgentTool());
        agent.AddTool(services.ReadHistory.AgentTool());
        agent.AddTool(services.ListHistory.AgentTool());
        agent.AddTool(services.SearchHistory.AgentTool());
    }
}
