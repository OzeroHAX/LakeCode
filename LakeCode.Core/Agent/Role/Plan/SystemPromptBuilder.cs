using LakeCode.Core.Agent.Prompt;
using Sections = LakeCode.Core.Agent.Prompt.Sections;

namespace LakeCode.Core.Agent.Role.Plan;

internal static class SystemPromptBuilder
{
    public static string Build(SystemPromptContext context, InstructionsReader instructions) =>
        SystemPromptComposer.Compose(
            Sections.Scope.Text,
            Sections.ProjectInvestigation.Text,
            Sections.CapabilityLimits.Text,
            Sections.Safety.Text,
            Sections.Answers.Text,
            Sections.Delegation.Text,
            Prompts.Instructions.Text,
            Prompts.PlanTemplate.Text,
            Sections.ToolUse.ForPlan,
            instructions.Global,
            instructions.Project,
            Sections.ExecutionContext.ForWorkspace(context));
}
