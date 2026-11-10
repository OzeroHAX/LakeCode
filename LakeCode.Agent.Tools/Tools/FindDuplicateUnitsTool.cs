using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>Finds repeated code units using Tree-sitter structure.</summary>
public sealed class FindDuplicateUnitsTool(WorkspaceFiles files) : AgentTool
{
    public override string Name => "find_duplicate_units";

    public override string Description =>
        "Find repeated language-neutral code units using Tree-sitter structure. Use this instead of broad regex " +
        "searches for duplicate code. Comparison is exact or whitespace-normalized; cross-language grouping is heuristic.";

    public override AgentToolMetadata Metadata { get; } = new(AgentToolEffects.ReadsWorkspace);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("path", "Workspace-relative or absolute directory path; access is decided by pre-tool policy.", ".")
        .Text("glob", "Relative path glob, for example **/*.py or **/*.{cs,java}.", "**/*")
        .Text("unitKind", "Unit category: any, callable, type, declaration, or block.", "callable")
        .Text("comparison", "Comparison: exact or normalized. Normalized ignores whitespace only.", "normalized")
        .Boolean("crossLanguage", "Allow grouping units from different grammars. This is structural and heuristic.", false)
        .Integer("minLines", "Minimum unit size in source lines.", minimum: 1, defaultValue: 3)
        .Integer("top", "Maximum duplicate groups to return, from 1 to 50.", minimum: 1, maximum: 50, defaultValue: 50));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var path = arguments.String("path", ".");
        var glob = arguments.String("glob", "**/*");
        var unitKind = arguments.String("unitKind", "callable");
        var comparison = arguments.String("comparison", "normalized");
        var crossLanguage = arguments.Boolean("crossLanguage", false);
        var minLines = arguments.Integer("minLines", 3);
        var top = arguments.Integer("top", 50);

        return SearchSupport.RunAsync(async () =>
        {
            if (!Enum.TryParse<DuplicateUnitKind>(unitKind, true, out var parsedKind))
            {
                throw new ArgumentException("unitKind must be any, callable, type, declaration, or block.");
            }

            if (!Enum.TryParse<DuplicateComparison>(comparison, true, out var parsedComparison))
            {
                throw new ArgumentException("comparison must be exact or normalized.");
            }

            if (top is < 1 or > 50)
            {
                throw new ArgumentOutOfRangeException(nameof(top));
            }

            var source = new WorkspaceSearchSource(files, path);
            var result = await SearchSupport.Engine(source)
                .FindDuplicatesAsync(
                    new DuplicateRequest
                    {
                        Filter = new SearchFilter { Include = [glob] },
                        UnitKind = parsedKind,
                        Comparison = parsedComparison,
                        CrossLanguage = crossLanguage,
                        MinLines = minLines,
                        Top = top,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var output = new System.Text.StringBuilder();
            output.AppendLine(
                $"Duplicate groups: {result.Items.Count}; complete: {result.Complete}; " +
                $"comparison: {parsedComparison}; crossLanguage: {crossLanguage}.");

            foreach (var group in result.Items)
            {
                output.AppendLine(
                    $"{group.Kind}, language={group.Language ?? "mixed"}, units={group.Units.Count}, " +
                    $"similarity={group.Similarity:0.###}");

                foreach (var unit in group.Units)
                {
                    output.AppendLine($"  {source.Display(unit.Path)}:{unit.StartLine}-{unit.EndLine} {unit.Label}");
                }

                if (output.Length >= WorkspaceFiles.MaxOutputCharacters - 256)
                {
                    output.AppendLine("[Output truncated; reduce top or narrow glob.]");
                    break;
                }
            }

            if (result.Issues.Count > 0)
            {
                output.AppendLine($"[Analysis incomplete: {result.Issues.Count} skipped files.]");

                foreach (var issue in result.Issues.Take(10))
                {
                    output.AppendLine($"  skipped {source.Display(issue.Path)}: {issue.Reason}");
                }
            }

            return output.ToString();
        });
    }
}
