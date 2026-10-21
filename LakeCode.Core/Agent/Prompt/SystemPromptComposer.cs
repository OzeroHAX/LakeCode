namespace LakeCode.Core.Agent.Prompt;

/// <summary>Joins the sections explicitly selected by a role; does not select a mode or read instructions.</summary>
internal static class SystemPromptComposer
{
    public static string Compose(params string[] sections) =>
        string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
}
