using LakeCode.Core.Agent.Prompt;
using Sections = LakeCode.Core.Agent.Prompt.Sections;

namespace LakeCode.Core.Agent.Subagents;

internal static class SystemPromptBuilder
{
    public static string Build(SystemPromptContext context, InstructionsReader instructions) =>
        SystemPromptComposer.Compose(
            Sections.Scope.Text,
            Sections.ProjectInvestigation.Text,
            Sections.ToolUse.Text,
            Sections.CapabilityLimits.Text,
            Sections.Safety.Text,
            Sections.Answers.Text,
            Sections.Delegation.Text,
            instructions.Global,
            instructions.Project,
            Sections.ExecutionContext.ForWorkspace(context),
            Prompts.Instructions.Text);
}
