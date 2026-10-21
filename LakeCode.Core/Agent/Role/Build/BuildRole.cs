using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Prompt;
using LakeCode.Core.Agent.Tools.Registration;

namespace LakeCode.Core.Agent.Role.Build;

internal sealed class BuildRole : AgentRole
{
    public BuildRole(SystemPromptContext context, InstructionsReader instructions, AgentToolServices services)
        : base(AgentMode.Build, new AgentConfiguration(SystemPromptBuilder.Build(context, instructions), services.Files)
        { UseSkills = true })
    {
        var agent = Configuration.Tools;
        agent.AddTool(services.ReadFile.AgentTool());
        agent.AddTool(services.ListDirectory.AgentTool());
        agent.AddTool(services.Grep.AgentTool());
        agent.AddTool(services.WriteFile.AgentTool());
        agent.AddTool(services.EditFile.AgentTool());
        agent.AddTool(services.Bash.AgentTool());
        agent.AddTool(services.ImageGeneration.AgentTool());
        agent.AddTool(services.Eval.AgentTool()).EvalAllowed(false);
        agent.AddTool(services.WebSearch.AgentTool());
        agent.AddTool(services.ReadWeb.AgentTool());
        agent.AddTool(services.Ask.AgentTool());
        agent.AddTool(services.ReadHistory.AgentTool());
        agent.AddTool(services.ListHistory.AgentTool());
        agent.AddTool(services.SearchHistory.AgentTool());
        agent.AddTool(services.Delegate.AgentTool());
        agent.AddTool(services.SendSubagentMessage.AgentTool());
        agent.AddTool(services.WaitSubagents.AgentTool());
    }
}
