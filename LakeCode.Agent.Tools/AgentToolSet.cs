using LakeCode.Agent.Tools.Bash;
using LakeCode.Agent.Tools.Files;
using LakeCode.Agent.Tools.Web;
namespace LakeCode.Agent.Tools;

/// <summary>
/// Default capability set. Composition (which subset, which mode, which hooks) belongs to the
/// orchestration layer; this type only wires tools that share the same workspace and host services.
/// </summary>
public sealed class AgentToolSet : IDisposable
{
    private AgentToolSet(
        WorkspaceFiles files,
        IReadOnlyList<IAgentTool> tools,
        WebClient web,
        EvalTool? eval)
    {
        Files = files;
        Tools = tools;
        Web = web;
        EvalWorker = eval;
    }

    public WorkspaceFiles Files { get; }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public WebClient Web { get; }

    /// <summary>Present only when eval was requested; it owns a worker process.</summary>
    public EvalTool? EvalWorker { get; }

    public AgentToolCatalog Catalog => AgentToolCatalog.Create(Tools);

    /// <summary>
    /// Creates the read, write, search, bash, ask and web capabilities for one session.
    /// Eval is opt-in because it owns a worker and is not needed by every mode.
    /// Set supportsImageInput from the model captured for the run; false makes image reads text-only.
    /// </summary>
    public static AgentToolSet Create(
        WorkspaceFiles files,
        string sessionId,
        Func<AskQuestion[], CancellationToken, Task<string[][]>>? ask = null,
        Func<string, System.Text.Json.JsonElement, CancellationToken, Task<string>>? invokeTool = null,
        Func<string>? describeTools = null,
        bool supportsImageInput = true)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sessionId);

        var web = new WebClient(sessionId);

        var tools = new List<IAgentTool>
        {
            new ReadFileTool(files, supportsImageInput),
            new ListDirectoryTool(files),
            new SearchTextTool(files),
            new FindFilesTool(files),
            new OutlineFileTool(files),
            new TraceCodeTool(files),
            new FindDuplicateUnitsTool(files),
            new WriteFileTool(files),
            new EditFileTool(files),
            new BashTool(files),
            new AskTool(ask),
            new WebSearchTool(web),
            new ReadWebTool(web),
        };

        EvalTool? eval = null;

        if (invokeTool is not null)
        {
            eval = new EvalTool(
                files.Root,
                invokeTool,
                describeTools ?? (() => "[]"));

            tools.Add(eval);
        }

        return new AgentToolSet(files, tools, web, eval);
    }

    public void Dispose() => EvalWorker?.Dispose();
}
