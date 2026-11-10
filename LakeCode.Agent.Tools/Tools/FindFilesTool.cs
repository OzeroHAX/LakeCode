using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>Ranks likely files by name and path terms with compact outlines.</summary>
public sealed class FindFilesTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "find_files";

    public override string Description =>
        "Rank likely files by normalized basename/path terms, with reasons and compact syntax outlines. " +
        "Not exhaustive text search or typo correction. Use to discover likely files before targeted search/read.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("query", "Topic/name/path terms, e.g. conversation service.")
        .Text("path", "Workspace-relative or absolute file/directory; access is decided by pre-tool policy.", ".")
        .Text("glob", "Relative path glob, e.g. **/*.cs.", "**/*")
        .Integer("top", "Maximum ranked files, 1-50.", minimum: 1, maximum: 50, defaultValue: 10));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var query = arguments.RequireString("query");
        var path = arguments.String("path", ".");
        var glob = arguments.String("glob", "**/*");
        var top = arguments.Integer("top", 10);

        return SearchSupport.RunAsync(async () =>
        {
            SearchSupport.ValidateTop(top);

            var source = new WorkspaceSearchSource(files, path);
            var result = await SearchSupport.Engine(source)
                .FindAsync(new FindRequest(query) { Top = top, Filter = SearchSupport.Filter(glob) }, cancellationToken)
                .ConfigureAwait(false);

            var output = new SearchOutput(source);
            output.Add($"Ranked files: {result.Items.Count} (top {top}; not exhaustive relevance).");
            output.Issues(result.Issues);

            foreach (var file in result.Items)
            {
                output.Ranked(file);
            }

            return output.ToString();
        });
    }
}
