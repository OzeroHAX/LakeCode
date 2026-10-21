namespace LakeCode.Core.Agent.Prompt;

/// <summary>Reads instructions once when constructing a session, not when switching roles.</summary>
internal sealed class InstructionsReader
{
    public string Global { get; }
    public string Project { get; }

    public InstructionsReader(string workspaceRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Global = string.IsNullOrEmpty(home) ? "" : Read(Path.Combine(home, ".agents", "AGENTS.md"), "Global instructions");
        Project = Read(Path.Combine(workspaceRoot, "AGENTS.md"), "Project instructions");
    }

    private static string Read(string path, string heading)
    {
        string content;
        try { content = File.ReadAllText(path); }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
        return string.IsNullOrWhiteSpace(content) ? "" : $"# {heading}\nSource: {path}\n\n{content}\n";
    }
}
