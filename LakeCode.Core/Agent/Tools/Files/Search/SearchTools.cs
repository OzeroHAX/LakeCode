using System.ComponentModel;
using System.Text;
using LakeCode.Search;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent.Tools.Search;

internal sealed class SearchTools(WorkspaceFiles files)
{
    public IEnumerable<AIFunction> AgentTools()
    {
        yield return AIFunctionFactory.Create(SearchTextAsync, name: "search_text");
        yield return AIFunctionFactory.Create(FindFilesAsync, name: "find_files");
        yield return AIFunctionFactory.Create(OutlineFileAsync, name: "outline_file");
        yield return AIFunctionFactory.Create(TraceCodeAsync, name: "trace_code");
        yield return AIFunctionFactory.Create(FindDuplicatesAsync, name: "find_duplicate_units");
    }

    [Description("Exact UTF-8 literal or per-line regex search with file/syntax grouping. Scope with path and glob. External paths require permission; hidden configured directories and symlinks are skipped. Reports skipped files and output truncation. Budget overflow is an error: narrow the scope. Use instead of shell grep/rg.")]
    public Task<string> SearchTextAsync(
        [Description("Literal text or regex pattern.")] string pattern,
        [Description("Workspace-relative or absolute file/directory; external paths require confirmation.")] string path = ".",
        [Description("Glob relative to the selected directory, e.g. **/*.cs; for a file, matches its basename.")] string glob = "**/*",
        [Description("Use .NET regex rather than exact literal matching.")] bool useRegex = false,
        [Description("Case-insensitive matching.")] bool ignoreCase = false,
        [Description("Matching-line budget, 1–500. Exceeding it is an error, not a partial success.")] int maxMatches = 100,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (maxMatches is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxMatches));
        var source = new WorkspaceSearchSource(files, path);
        var result = await Engine(source, maxMatches).GrepAsync(new(pattern)
        { Regex = useRegex, IgnoreCase = ignoreCase, Filter = Filter(glob) }, cancellationToken);
        var output = new SearchOutput(source);
        output.Add($"Matches: {result.Items.Sum(f => f.Matches.Count)} lines in {result.Items.Count} files.");
        output.Issues(result.Issues);
        foreach (var file in result.Items)
        {
            output.Add(source.Display(file.Outline.Path));
            foreach (var match in file.Matches)
            {
                output.Add($"  @{match.Line}: {match.Text}");
                if (match.Enclosing is { } item) output.Add($"    in {item.Kind} {item.Label} @{item.StartLine}-{item.EndLine}");
            }
            output.Outline(file.Outline, 8);
        }
        return output.ToString();
    });

    [Description("Rank likely files by normalized basename/path terms, with reasons and compact syntax outlines. Not exhaustive text search or typo correction. Use to discover likely files before targeted search/read. Same file permissions and exclusions as search_text.")]
    public Task<string> FindFilesAsync(
        [Description("Topic/name/path terms, e.g. conversation service.")] string query,
        [Description("Workspace-relative or absolute file/directory; external paths require confirmation.")] string path = ".",
        [Description("Relative path glob, e.g. **/*.cs.")] string glob = "**/*",
        [Description("Maximum ranked files, 1–50.")] int top = 10,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        ValidateTop(top);
        var source = new WorkspaceSearchSource(files, path);
        var result = await Engine(source).FindAsync(new(query) { Top = top, Filter = Filter(glob) }, cancellationToken);
        var output = new SearchOutput(source);
        output.Add($"Ranked files: {result.Items.Count} (top {top}; not exhaustive relevance).");
        output.Issues(result.Issues);
        foreach (var file in result.Items) output.Ranked(file);
        return output.ToString();
    });

    [Description("Show a known UTF-8 file's Tree-sitter syntax structure with 1-based line ranges, without full source bodies. Generic syntax units, not semantic symbols. Unknown extensions are reported; native parser errors are not hidden. Use read_file to verify actual bodies.")]
    public Task<string> OutlineFileAsync(
        [Description("Workspace-relative or absolute file path; external paths require confirmation.")] string path,
        [Description("Optional grammar ID, e.g. c-sharp, cpp, python, typescript, tsx, jsdoc. Null uses extension detection.")] string? language = null,
        [Description("Maximum displayed structure items, 1–500; omissions are reported.")] int maxItems = 100,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (maxItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxItems));
        var source = new WorkspaceSearchSource(files, path);
        if (Directory.Exists(files.ResolveRead(path))) throw new ArgumentException("outline_file requires a file path.");
        var outline = await Engine(source).OutlineAsync(source.FileId, language, cancellationToken);
        var output = new SearchOutput(source);
        output.Add(source.Display(outline.Path));
        output.Outline(outline, maxItems);
        return output.ToString();
    });

    [Description("Lexical/structural investigation: rank files and return focused code regions for a subject and relation. Heuristic clues, NOT a resolved call graph or semantic references. All normalized subject terms must occur on one line. Use search_text for exact/exhaustive matching; verify candidate bodies with read_file.")]
    public Task<string> TraceCodeAsync(
        [Description("Identifier or subject terms present in source.")] string subject,
        [Description("One of: defined, called_from, triggered_from, rendered, populated, comes_from, handled, implementation.")] string relation,
        [Description("Workspace-relative or absolute file/directory; external paths require confirmation.")] string path = ".",
        [Description("Relative path glob, e.g. **/*.cs.")] string glob = "**/*",
        [Description("Optional additional terms used as ranking clues.")] string[]? support = null,
        [Description("Maximum ranked files, 1–50.")] int top = 10,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        ValidateTop(top);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        var name = relation.Replace("_", "", StringComparison.Ordinal);
        if (!Enum.GetNames<TraceRelation>().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            !Enum.TryParse<TraceRelation>(name, true, out var parsed)) throw new ArgumentException("Unsupported trace relation.");
        var source = new WorkspaceSearchSource(files, path);
        var result = await Engine(source).TraceAsync(new(subject, parsed)
        { Top = top, Filter = Filter(glob), Support = support?.ToArray() ?? [] }, cancellationToken);
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
                for (var i = 0; i < lines.Length; i++) output.Add($"  @{region.StartLine + i}: {lines[i]}");
            }
        }
        return output.ToString();
    });

    [Description("Find repeated language-neutral code units using Tree-sitter structure. Supports callable, type, declaration, block, or any units across all recognized grammars. Comparison is exact or whitespace-normalized; crossLanguage is opt-in and heuristic. Use this instead of broad regex searches for duplicate code.")]
    public Task<string> FindDuplicatesAsync(
        [Description("Workspace-relative or absolute directory path; external paths require confirmation.")] string path = ".",
        [Description("Relative path glob, for example **/*.py or **/*.{cs,java}. The default scans all supported text files.")] string glob = "**/*",
        [Description("Unit category: any, callable, type, declaration, or block.")] string unitKind = "callable",
        [Description("Comparison: exact or normalized. Normalized ignores whitespace only.")] string comparison = "normalized",
        [Description("Allow grouping units from different grammars. This is structural and heuristic.")] bool crossLanguage = false,
        [Description("Minimum unit size in source lines.")] int minLines = 3,
        [Description("Maximum duplicate groups to return, from 1 to 50.")] int top = 50,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
    {
        if (!Enum.TryParse<DuplicateUnitKind>(unitKind, true, out var parsedKind))
            throw new ArgumentException("unitKind must be any, callable, type, declaration, or block.");
        if (!Enum.TryParse<DuplicateComparison>(comparison, true, out var parsedComparison))
            throw new ArgumentException("comparison must be exact or normalized.");
        var source = new WorkspaceSearchSource(files, path);
        var result = await Engine(source).FindDuplicatesAsync(new DuplicateRequest
        {
            Filter = new SearchFilter { Include = [glob] },
            UnitKind = parsedKind,
            Comparison = parsedComparison,
            CrossLanguage = crossLanguage,
            MinLines = minLines,
            Top = top
        }, cancellationToken);
        var output = new StringBuilder();
        output.AppendLine($"Duplicate groups: {result.Items.Count}; complete: {result.Complete}; comparison: {parsedComparison}; crossLanguage: {crossLanguage}.");
        foreach (var group in result.Items)
        {
            output.AppendLine($"{group.Kind}, language={(group.Language ?? "mixed")}, units={group.Units.Count}, similarity={group.Similarity:0.###}");
            foreach (var unit in group.Units)
                output.AppendLine($"  {source.Display(unit.Path)}:{unit.StartLine}-{unit.EndLine} {unit.Label}");
            if (output.Length >= WorkspaceFiles.MaxOutputCharacters - 256)
            {
                output.AppendLine("[Output truncated; reduce top or narrow glob.]");
                break;
            }
        }
        if (result.Issues.Count > 0)
        {
            output.AppendLine($"[Analysis incomplete: {result.Issues.Count} skipped files.]");
            foreach (var issue in result.Issues.Take(10)) output.AppendLine($"  skipped {source.Display(issue.Path)}: {issue.Reason}");
        }
        return output.ToString();
    });

    private static SearchEngine Engine(WorkspaceSearchSource source, int maxResults = 1000) => new(source,
        new SearchLimits { MaxFileBytes = WorkspaceFiles.MaxFileBytes, MaxResults = maxResults });
    private static SearchFilter Filter(string glob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);
        return new() { Include = [glob] };
    }
    private static void ValidateTop(int top)
    {
        if (top is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(top));
    }
    private static async Task<string> ExecuteAsync(Func<Task<string>> action)
    {
        try { return await WorkspaceFiles.ExecuteAsync(action); }
        catch (SearchLimitException error) { return "Error: " + error.Message; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        { return "Error: Search/structure analysis failed: " + error.Message; }
    }

    private sealed class SearchOutput(WorkspaceSearchSource source)
    {
        private readonly StringBuilder _text = new();
        private bool _truncated;
        public void Add(string line)
        {
            if (_truncated) return;
            const string marker = "\n[Output truncated; narrow the scope or request fewer items. Search output is incomplete.]";
            var remaining = WorkspaceFiles.MaxOutputCharacters - marker.Length - _text.Length;
            if (line.Length + 1 > remaining)
            {
                _text.Append(line.AsSpan(0, Math.Max(0, remaining))).Append(marker);
                _truncated = true;
            }
            else _text.Append(line).Append('\n');
        }
        public void Issues(IReadOnlyList<SearchIssue> issues)
        {
            if (issues.Count == 0) return;
            Add($"[Search incomplete: {issues.Count} skipped files.]");
            foreach (var issue in issues) Add($"  skipped {source.Display(issue.Path)}: {issue.Reason}");
        }
        public void Outline(FileOutline outline, int max)
        {
            Add($"  language: {outline.Language ?? "unsupported (text search only)"}; lines: {outline.LineCount}; syntax errors: {outline.HasSyntaxErrors}");
            foreach (var item in outline.Items.Take(max)) Add($"    {item.Kind} {item.Label} @{item.StartLine}-{item.EndLine}");
            if (outline.Items.Count > max) Add($"    [{outline.Items.Count - max} structure items omitted]");
        }
        public void Ranked(RankedFile file)
        {
            Add($"{source.Display(file.Path)} (score {file.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            foreach (var reason in file.Reasons) Add("  why: " + reason);
            Outline(file.Outline, 8);
        }
        public override string ToString() => _text.ToString();
    }
}
