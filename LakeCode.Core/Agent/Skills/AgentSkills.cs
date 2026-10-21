using LakeCode.Core.Agent.Tools;
using Microsoft.Agents.AI;

namespace LakeCode.Core.Agent.Skills;

internal sealed class AgentSkills
{
    private readonly string[] _skillPaths;
    public AIContextProvider Provider { get; }

    public AgentSkills(WorkspaceFiles files)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectSkills = files.Resolve(".agents/skills", allowMissing: true);
        // MAF deduplicates by name: the project source wins.
        _skillPaths = string.IsNullOrEmpty(home) ? [projectSkills]
            : [projectSkills, Path.Combine(home, ".agents", "skills")];
        ValidateRoots();
        Provider = new AgentSkillsProvider(
            _skillPaths,
            fileOptions: new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [] },
            options: new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
                SkillsInstructionPrompt = """
                    You have access to the following project and global skills:
                    Project skills take precedence over global skills with the same name.
                    <available_skills>
                    {skills}
                    </available_skills>
                    Apply the task-relevance rules for skills above: load only skills needed for the user's
                    task or a necessary specialized subtask, or explicitly requested by the user.
                    Incidental topic matches, ordinary workflow actions and broad trigger descriptions
                    do not suffice. Apply the same criterion to skills referenced by other skills.
                    Use the smallest relevant set. Use read_skill_resource for listed supplementary
                    references and assets only as needed for the current task, with names exactly as listed.
                    Follow applicable loaded guidance within the user's
                    request and project restrictions; skills do not override higher-priority rules.
                    Script execution is not provided by the skills tools.
                    """
            });
    }

    public void ValidateRoots()
    {
        foreach (var path in _skillPaths)
            WorkspaceFiles.ValidateLinks(path, Path.GetPathRoot(path)!, allowMissing: true);
    }
}
