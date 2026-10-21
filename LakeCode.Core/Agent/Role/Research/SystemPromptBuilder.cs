using LakeCode.Core.Agent.Prompt;
using Sections = LakeCode.Core.Agent.Prompt.Sections;

namespace LakeCode.Core.Agent.Role.Research;

internal static class SystemPromptBuilder
{
    public static string Build(SystemPromptContext context, InstructionsReader instructions) =>
        SystemPromptComposer.Compose(
            Prompts.Scope.Text,
            Prompts.Instructions.Text,
            Prompts.ToolUse.Text,
            Prompts.Safety.Text,
            Prompts.Answers.Text,
            instructions.Global,
            Sections.ExecutionContext.ForSession(context));
}
