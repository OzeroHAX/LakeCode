using LakeCode.Core.Agent.Compaction;
using LakeCode.Core.Agent.Delegation;
using LakeCode.Core.Agent.Subagents.Tools;
using LakeCode.Core.Agent.Tools.Ask;
using LakeCode.Core.Agent.Tools.Bash;
using LakeCode.Core.Agent.Tools.EditFile;
using LakeCode.Core.Agent.Tools.Eval;
using LakeCode.Core.Agent.Tools.Execution;
using LakeCode.Core.Agent.Tools.Grep;
using LakeCode.Core.Agent.Tools.History;
using LakeCode.Core.Agent.Tools.ImageGeneration;
using LakeCode.Core.Agent.Tools.ListDirectory;
using LakeCode.Core.Agent.Tools.ReadFile;
using LakeCode.Core.Agent.Tools.ReadWeb;
using LakeCode.Core.Agent.Tools.WebSearch;
using LakeCode.Core.Agent.Tools.WriteFile;
using WebClient = LakeCode.Core.Agent.Tools.Web.WebClient;

namespace LakeCode.Core.Agent.Tools.Registration;

/// <summary>Constructs tool instances once for a file context; roles only register their AgentTool functions.</summary>
internal sealed class AgentToolServices
{
    private readonly DelegationCoordinator _delegation;
    private readonly HistoryProjection _history;
    private readonly WebClient _web;
    private readonly string _sessionId;
    private readonly Func<Settings.ImageGenerationConfiguration?> _imageGeneration;

    public WorkspaceFiles Files { get; }
    public ToolInvocationRunner Invocations { get; }
    public ReadFileTool ReadFile { get; }
    public ListDirectoryTool ListDirectory { get; }
    public GrepTool Grep { get; }
    public WriteFileTool WriteFile { get; }
    public EditFileTool EditFile { get; }
    public BashTool Bash { get; }
    public EvalTool Eval { get; }
    public ImageGenerationTool ImageGeneration { get; }
    public WebSearchTool WebSearch { get; }
    public ReadWebTool ReadWeb { get; }
    public AskTool Ask { get; }
    public ReadHistoryTool ReadHistory { get; }
    public ListHistoryTool ListHistory { get; }
    public SearchHistoryTool SearchHistory { get; }
    public DelegateTool Delegate { get; }
    public SendSubagentMessageTool SendSubagentMessage { get; }
    public WaitSubagentsTool WaitSubagents { get; }

    public AgentToolServices(WorkspaceFiles files, EvalTool eval, DelegationCoordinator delegation,
        HistoryProjection history, ToolInvocationRunner invocations, AskTool ask, string sessionId,
        Func<Settings.ImageGenerationConfiguration?> imageGeneration)
        : this(files, eval, delegation, history, invocations, ask, sessionId, imageGeneration, new WebClient(sessionId)) { }

    private AgentToolServices(WorkspaceFiles files, EvalTool eval, DelegationCoordinator delegation,
        HistoryProjection history, ToolInvocationRunner invocations, AskTool ask, string sessionId,
        Func<Settings.ImageGenerationConfiguration?> imageGeneration, WebClient web)
    {
        Files = files;
        Eval = eval;
        Ask = ask;
        Invocations = invocations;
        _delegation = delegation;
        _history = history;
        _sessionId = sessionId;
        _imageGeneration = imageGeneration;
        _web = web;
        ReadFile = new ReadFileTool(files);
        ListDirectory = new ListDirectoryTool(files);
        Grep = new GrepTool(files);
        WriteFile = new WriteFileTool(files, invocations.ReportFileChange);
        EditFile = new EditFileTool(files, invocations.ReportFileChange);
        Bash = new BashTool(files, invocations.ReportBashOutput);
        ImageGeneration = new ImageGenerationTool(files, imageGeneration, sessionId);
        WebSearch = new WebSearchTool(web);
        ReadWeb = new ReadWebTool(web);
        ReadHistory = new ReadHistoryTool(history);
        ListHistory = new ListHistoryTool(history);
        SearchHistory = new SearchHistoryTool(history);
        Delegate = new DelegateTool(delegation);
        SendSubagentMessage = new SendSubagentMessageTool(delegation);
        WaitSubagents = new WaitSubagentsTool(delegation);
    }

    /// <summary>Used once to compose Research; workers, permissions on the original context and shared clients stay intact.</summary>
    public AgentToolServices WithFiles(WorkspaceFiles files) =>
        new(files, Eval, _delegation, _history, Invocations, Ask, _sessionId, _imageGeneration, _web);
}
