using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>Heuristic subject/relation investigation returning focused code regions.</summary>
public sealed class TraceCodeTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "trace_code";

    public override string Description =>
        "Lexical/structural investigation: rank files and return focused code regions for a subject and relation. " +
        "Heuristic clues, NOT a resolved call graph or semantic references. Use search_text for exact matching.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("subject", "Identifier or subject terms present in source.")
        .Text("relation", "One of: defined, called_from, triggered_from, rendered, populated, comes_from, handled, implementation.")
        .Text("path", "Workspace-relative or absolute file/directory; access is decided by pre-tool policy.", ".")
        .Text("glob", "Relative path glob, e.g. **/*.cs.", "**/*")
        .TextArray("support", "Optional additional terms used as ranking clues.")
        .Integer("top", "Maximum ranked files, 1-50.", minimum: 1, maximum: 50, defaultValue: 10));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var subject = arguments.RequireString("subject");
        var relation = arguments.RequireString("relation");
        var path = arguments.String("path", ".");
        var glob = arguments.String("glob", "**/*");
        var support = arguments.OptionalStringArray("support");
        var top = arguments.Integer("top", 10);

        return SearchSupport.RunAsync(async () =>
        {
            SearchSupport.ValidateTop(top);
            ArgumentException.ThrowIfNullOrWhiteSpace(relation);

            var name = relation.Replace("_", "", StringComparison.Ordinal);

            if (!Enum.GetNames<TraceRelation>().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                !Enum.TryParse<TraceRelation>(name, true, out var parsed))
            {
                throw new ArgumentException("Unsupported trace relation.");
            }

            var source = new WorkspaceSearchSource(files, path);
            var result = await SearchSupport.Engine(source)
                .TraceAsync(
                    new TraceRequest(subject, parsed)
                    {
                        Top = top,
                        Filter = SearchSupport.Filter(glob),
                        Support = support?.ToArray() ?? [],
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var output = new SearchOutput(source);
            output.Add($"Heuristic trace: {result.Items.Count} ranked files (not semantic reference resolution).");
            output.Issues(result.Issues);

            foreach (var file in result.Items)
            {
                output.Ranked(file.File);

                foreach (var region in file.Regions)
                {
                    output.Add($"  region @{region.StartLine}-{region.EndLine}");
                    var lines = region.Text.Split('\n');

                    for (var i = 0; i < lines.Length; i++)
                    {
                        output.Add($"  @{region.StartLine + i}: {lines[i]}");
                    }
                }
            }

            return output.ToString();
        });
    }
}
