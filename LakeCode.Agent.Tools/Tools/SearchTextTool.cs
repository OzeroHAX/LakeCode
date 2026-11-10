using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>Exact or regex search over the workspace with file and syntax grouping.</summary>
public sealed class SearchTextTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "search_text";

    public override string Description =>
        "Exact UTF-8 literal or per-line regex search with file/syntax grouping. Scope with path and glob. " +
        "Use instead of shell grep/rg.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("pattern", "Literal text or regex pattern.")
        .Text("path", "Workspace-relative or absolute file/directory; access is decided by pre-tool policy.", ".")
        .Text("glob", "Glob relative to the selected directory, e.g. **/*.cs; for a file, matches its basename.", "**/*")
        .Boolean("useRegex", "Use .NET regex rather than exact literal matching.", false)
        .Boolean("ignoreCase", "Case-insensitive matching.", false)
        .Integer("maxMatches", "Matching-line budget, 1-500. Exceeding it is an error, not a partial success.",
            minimum: 1, maximum: 500, defaultValue: 100));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var pattern = arguments.RequireString("pattern", allowEmpty: true);
        var path = arguments.String("path", ".");
        var glob = arguments.String("glob", "**/*");
        var useRegex = arguments.Boolean("useRegex", false);
        var ignoreCase = arguments.Boolean("ignoreCase", false);
        var maxMatches = arguments.Integer("maxMatches", 100);

        return SearchSupport.RunAsync(async () =>
        {
            if (maxMatches is < 1 or > 500)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMatches));
            }

            var source = new WorkspaceSearchSource(files, path);
            var result = await SearchSupport.Engine(source, maxMatches)
                .GrepAsync(
                    new GrepRequest(pattern) { Regex = useRegex, IgnoreCase = ignoreCase, Filter = SearchSupport.Filter(glob) },
                    cancellationToken)
                .ConfigureAwait(false);

            var output = new SearchOutput(source);
            output.Add($"Matches: {result.Items.Sum(file => file.Matches.Count)} lines in {result.Items.Count} files.");
            output.Issues(result.Issues);

            foreach (var file in result.Items)
            {
                output.Add(source.Display(file.Outline.Path));

                foreach (var match in file.Matches)
                {
                    output.Add($"  @{match.Line}: {match.Text}");

                    if (match.Enclosing is { } item)
                    {
                        output.Add($"    in {item.Kind} {item.Label} @{item.StartLine}-{item.EndLine}");
                    }
                }

                output.Outline(file.Outline, 8);
            }

            return output.ToString();
        });
    }
}
