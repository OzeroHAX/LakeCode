using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>Shows one file's Tree-sitter structure without full bodies.</summary>
public sealed class OutlineFileTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "outline_file";

    public override string Description =>
        "Show a known UTF-8 file's Tree-sitter syntax structure with 1-based line ranges, without full source bodies. " +
        "Generic syntax units, not semantic symbols. Unknown extensions are reported; native parser errors are not hidden.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("path", "Workspace-relative or absolute file path; access is decided by pre-tool policy.")
        .Text("language", "Optional grammar ID, e.g. c-sharp, cpp, python, typescript, tsx, jsdoc. Null uses extension detection.")
        .Integer("maxItems", "Maximum displayed structure items, 1-500; omissions are reported.",
            minimum: 1, maximum: 500, defaultValue: 100));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var path = arguments.RequireString("path");
        var language = arguments.OptionalString("language");
        var maxItems = arguments.Integer("maxItems", 100);

        return SearchSupport.RunAsync(async () =>
        {
            if (maxItems is < 1 or > 500)
            {
                throw new ArgumentOutOfRangeException(nameof(maxItems));
            }

            var source = new WorkspaceSearchSource(files, path);

            if (Directory.Exists(files.Absolute(path)))
            {
                throw new ArgumentException("outline_file requires a file path.");
            }

            var outline = await SearchSupport.Engine(source)
                .OutlineAsync(source.FileId, language, cancellationToken)
                .ConfigureAwait(false);

            var output = new SearchOutput(source);
            output.Add(source.Display(outline.Path));
            output.Outline(outline, maxItems);

            return output.ToString();
        });
    }
}
