using LakeCode.Core.Agent.Execution;
using LakeCode.Core.Agent.Prompt;
using LakeCode.Core.Agent.Role.Plan.Tools;
using LakeCode.Core.Agent.Tools.Registration;

namespace LakeCode.Core.Agent.Role.Plan;

internal sealed class PlanRole : AgentRole
{
    private readonly SubmitPlanTool _submit;
    private readonly AgentToolServices _services;

    public PlanRole(SystemPromptContext context, InstructionsReader instructions, AgentToolServices services,
        Func<string, ImplementationPlan?> savePlan)
        : this(context, instructions, services, new SubmitPlanTool(savePlan)) { }

    private PlanRole(SystemPromptContext context, InstructionsReader instructions,
        AgentToolServices services, SubmitPlanTool submit)
        : base(AgentMode.Plan, new AgentConfiguration(SystemPromptBuilder.Build(context, instructions), services.Files)
        {
            UseSkills = true,
            AllowMultipleToolCalls = false,
            IsComplete = () => submit.Submitted,
            CompletionError = "Error: Plan already submitted; turn is complete."
        })
    {
        _submit = submit;
        _services = services;
        var agent = Configuration.Tools;
        agent.AddTool(services.ReadFile.AgentTool());
        agent.AddTool(services.ListDirectory.AgentTool());
        agent.AddTool(services.Grep.AgentTool());
        agent.AddTool(services.Bash.AgentTool())
            .Policy(ToolExecutionPolicy.RequireApproval);
        agent.AddTool(services.Eval.AgentTool())
            .Policy(ToolExecutionPolicy.RequireApproval)
            .EvalAllowed(false);
        agent.AddTool(submit.AgentTool());
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

    public override void BeginTurn(AgentMode? previousMode)
    {
        if (previousMode != AgentMode.Plan) _services.Eval.Reset();
        _submit.BeginTurn();
    }
}
