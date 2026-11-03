using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace LakeCode.Search;

/// <summary>Stateless, index-free retrieval. Each call owns its budgets, parsers and streams.</summary>
public sealed class SearchEngine
{
    private readonly ISearchSource _source;
    private readonly SearchLimits _limits;
    private readonly Func<string, string?>? _languageSelector;
    public static IReadOnlyList<Grammar> Grammars => StructureReader.Grammars;

    public SearchEngine(ISearchSource source, SearchLimits? limits = null, Func<string, string?>? languageSelector = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _languageSelector = languageSelector;
        _limits = limits ?? new();
        if (_limits.MaxFileBytes <= 0 || _limits.MaxTotalBytes <= 0 || _limits.MaxFiles <= 0 ||
            _limits.MaxNodes <= 0 || _limits.MaxResults <= 0 || _limits.MaxLineCharacters <= 0 ||
            _limits.Timeout <= TimeSpan.Zero || _limits.Timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public Task<SearchResult<GrepFile>> GrepAsync(GrepRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Pattern);
        var filter = Filter(request.Filter);
        return Execute(async op =>
        {
            var regex = request.Regex ? new Regex(request.Pattern, RegexOptions.CultureInvariant |
                (request.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(250)) : null;
            var result = new List<GrepFile>();
            await foreach (var path in op.Paths(filter))
            {
                var text = await op.Read(path);
                if (text is null) continue;
                var matches = new List<(int Line, string Text)>();
                var lines = Lines(text);
                for (var i = 0; i < lines.Length; i++)
                {
                    op.Check();
                    if (!(regex?.IsMatch(lines[i]) ?? lines[i].Contains(request.Pattern,
                        request.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
                    op.Result();
                    matches.Add((i + 1, lines[i]));
                }
                if (matches.Count == 0) continue;
                var outline = op.Outline(path, text);
                result.Add(new(outline, matches.Select(m => new TextMatch(m.Line, m.Text, Enclosing(outline, m.Line))).ToArray()));
            }
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Outline.Path, b.Outline.Path));
            return op.Finish(result);
        }, cancellationToken);
    }

    public Task<FileOutline> OutlineAsync(string path, string? language = null, CancellationToken cancellationToken = default) =>
        Execute(async op =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var text = await op.Read(path, required: true);
            return op.Outline(path, text!, language);
        }, cancellationToken);

    public Task<SearchResult<RankedFile>> FindAsync(FindRequest request, CancellationToken cancellationToken = default)
    {
        var terms = Terms(request.Query);
        ValidateTop(request.Top);
        var filter = Filter(request.Filter);
        return Execute(async op =>
        {
            var candidates = new List<(string Path, double Score, string[] Reasons)>();
            await foreach (var path in op.Paths(filter))
            {
                var (score, reasons) = PathScore(path, terms);
                if (score <= 0) continue;
                candidates.Add((path, score, reasons));
                // Bounded top-k: never retain the full repository manifest.
                candidates.Sort((a, b) => Compare(a.Score, a.Path, b.Score, b.Path));
                if (candidates.Count > request.Top) candidates.RemoveAt(candidates.Count - 1);
            }
            var results = new List<RankedFile>();
            foreach (var candidate in candidates)
            {
                var text = await op.Read(candidate.Path);
                if (text is null) continue;
                results.Add(new(candidate.Path, candidate.Score, candidate.Reasons, op.Outline(candidate.Path, text)));
            }
            return op.Finish(results);
        }, cancellationToken);
    }

    public Task<SearchResult<TraceFile>> TraceAsync(TraceRequest request, CancellationToken cancellationToken = default)
    {
        var subjects = Terms(request.Subject);
        ValidateTop(request.Top);
        if (!Enum.IsDefined(request.Relation)) throw new ArgumentOutOfRangeException(nameof(request.Relation));
        if (request.ContextLines is < 0 or > 1000 || request.FullRegionLines is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request));
        var support = request.Support.SelectMany(Terms).Distinct(StringComparer.Ordinal).ToArray();
        var hints = RelationTerms(request.Relation);
        var filter = Filter(request.Filter);
        return Execute(async op =>
        {
            var results = new List<TraceFile>();
            await foreach (var path in op.Paths(filter))
            {
                var text = await op.Read(path);
                if (text is null) continue;
                var lines = Lines(text);
                var hits = new List<int>();
                for (var i = 0; i < lines.Length; i++)
                {
                    op.Check();
                    var normalized = Normalize(lines[i]);
                    if (subjects.All(t => normalized.Contains(t, StringComparison.Ordinal))) hits.Add(i + 1);
                }
                if (hits.Count == 0) continue;
                var outline = op.Outline(path, text);
                var (score, pathReasons) = PathScore(path, subjects);
                var reasons = new List<string>(pathReasons) { $"subject matches on {hits.Count} lines" };
                score += 10 + Math.Min(10, hits.Count);
                var ranges = new List<(int Start, int End, StructureItem? Item)>();
                foreach (var hit in hits)
                {
                    op.Check();
                    var enclosing = Enclosing(outline, hit);
                    var start = Math.Max(1, hit - request.ContextLines);
                    var end = Math.Min(lines.Length, hit + request.ContextLines);
                    if (enclosing is not null && enclosing.EndLine - enclosing.StartLine + 1 <= request.FullRegionLines)
                        (start, end) = (enclosing.StartLine, enclosing.EndLine);
                    var regionText = string.Join('\n', lines[(start - 1)..end]);
                    var matchedHints = hints.Where(h => regionText.Contains(h, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matchedHints.Length > 0) { score += 3; reasons.Add("relation clues: " + string.Join(", ", matchedHints)); }
                    if (request.Relation == TraceRelation.Defined && enclosing is not null && hit == enclosing.StartLine)
                    { score += 8; reasons.Add("subject on structural declaration line"); }
                    foreach (var term in support)
                        if (Normalize(regionText).Contains(term, StringComparison.Ordinal)) { score += 2; reasons.Add("support: " + term); }
                    while (ranges.Count > 0 && start <= ranges[^1].End + 1)
                    {
                        var previous = ranges[^1];
                        ranges.RemoveAt(ranges.Count - 1);
                        start = Math.Min(previous.Start, start);
                        end = Math.Max(previous.End, end);
                        if (previous.Item != enclosing) enclosing = null;
                    }
                    ranges.Add((start, end, enclosing));
                }
                var regions = ranges.Select(r => new CodeRegion(r.Start, r.End,
                    string.Join('\n', lines[(r.Start - 1)..r.End]), r.Item)).ToArray();
                results.Add(new(new(path, score, reasons.Distinct().ToArray(), outline), regions));
                results.Sort((a, b) => Compare(a.File.Score, a.File.Path, b.File.Score, b.File.Path));
                if (results.Count > request.Top) results.RemoveAt(results.Count - 1);
            }
            foreach (var result in results) foreach (var _ in result.Regions) op.Result();
            return op.Finish(results);
        }, cancellationToken);
    }

    private void ValidateTop(int top)
    {
        if (top <= 0 || top > _limits.MaxResults) throw new ArgumentOutOfRangeException(nameof(top));
    }
    private Task<T> Execute<T>(Func<Operation, Task<T>> action, CancellationToken token) => Task.Run(async () =>
    {
        using var deadline = new CancellationTokenSource(_limits.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { var result = await action(new(_source, _limits, linked.Token, _languageSelector)); linked.Token.ThrowIfCancellationRequested(); return result; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new SearchLimitException("Search deadline exceeded."); }
    }, token);

    private static Matcher Filter(SearchFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var matcher = new Matcher(StringComparison.Ordinal);
        foreach (var include in filter.Include.Count == 0 ? ["**/*"] : filter.Include) matcher.AddInclude(include);
        foreach (var exclude in filter.Exclude) matcher.AddExclude(exclude);
        return matcher;
    }
    private static int Compare(double a, string ap, double b, string bp)
    { var order = b.CompareTo(a); return order != 0 ? order : StringComparer.Ordinal.Compare(ap, bp); }
    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static string[] Terms(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0).Distinct().ToArray();
        if (terms.Length == 0) throw new ArgumentException("Query must contain letters or digits.");
        return terms;
    }
    private static (double Score, string[] Reasons) PathScore(string path, string[] terms)
    {
        var normalized = Normalize(path);
        var basename = Normalize(Path.GetFileNameWithoutExtension(path));
        var reasons = new List<string>();
        double score = 0;
        foreach (var term in terms)
        {
            if (basename == term) { score += 12; reasons.Add("exact basename: " + term); }
            else if (basename.Contains(term, StringComparison.Ordinal)) { score += 8; reasons.Add("basename: " + term); }
            else if (normalized.Contains(term, StringComparison.Ordinal)) { score += 3; reasons.Add("path: " + term); }
        }
        return (score, reasons.ToArray());
    }
    private static StructureItem? Enclosing(FileOutline outline, int line) => outline.Items
        .Where(s => s.StartLine <= line && s.EndLine >= line).OrderBy(s => s.EndLine - s.StartLine).ThenByDescending(s => s.Depth).FirstOrDefault();
    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    private static string[] RelationTerms(TraceRelation relation) => relation switch
    {
        TraceRelation.Defined => ["class", "def", "fn", "function", "interface", "struct", "enum"],
        TraceRelation.CalledFrom => ["call", "invoke", "await"],
        TraceRelation.TriggeredFrom => ["event", "emit", "trigger", "dispatch", "subscribe"],
        TraceRelation.Rendered => ["render", "draw", "view", "widget", "display"],
        TraceRelation.Populated => ["set", "assign", "load", "populate", "="],
        TraceRelation.ComesFrom => ["read", "load", "config", "return", "source"],
        TraceRelation.Handled => ["handle", "handler", "process", "dispatch"],
        TraceRelation.Implementation => ["impl", "override", "implements", "class"],
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    private sealed class Operation(ISearchSource source, SearchLimits limits, CancellationToken token, Func<string, string?>? languageSelector)
    {
        private long _bytes;
        private int _files, _results;
        private readonly List<SearchIssue> _issues = [];
        public void Check() => token.ThrowIfCancellationRequested();
        public void Result() { if (++_results > limits.MaxResults) throw new SearchLimitException("Result budget exceeded; narrow the query."); }
        public async IAsyncEnumerable<string> Paths(Matcher filter)
        {
            await foreach (var path in source.EnumerateAsync(token).WithCancellation(token))
            {
                Check();
                if (++_files > limits.MaxFiles) throw new SearchLimitException("File enumeration budget exceeded.");
                if (filter.Match(path.Replace('\\', '/')).HasMatches) yield return path;
            }
        }
        public async Task<string?> Read(string path, bool required = false)
        {
            try
            {
                Check();
                await using var stream = await source.OpenReadAsync(path, token);
                using var buffer = new MemoryStream();
                var block = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(block, token)) != 0)
                {
                    Check();
                    _bytes += read;
                    if (_bytes > limits.MaxTotalBytes) throw new SearchLimitException("Cumulative read budget exceeded.");
                    if (buffer.Length + read > limits.MaxFileBytes) throw new SearchLimitException($"File read budget exceeded: {path}.");
                    buffer.Write(block, 0, read);
                }
                var text = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
                if (text.Contains('\0')) throw new InvalidDataException("Binary file.");
                foreach (var line in Lines(text))
                    if (line.Length > limits.MaxLineCharacters) throw new SearchLimitException($"Line length budget exceeded: {path}.");
                return text;
            }
            catch (Exception ex) when (!required && ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { _issues.Add(new(path, ex.Message)); return null; }
        }
        public FileOutline Outline(string path, string text, string? language = null) =>
            StructureReader.Read(path, text, language ?? languageSelector?.Invoke(path), Check, limits.MaxNodes);
        public SearchResult<T> Finish<T>(List<T> items) { Check(); return new(items.ToArray(), _issues.ToArray(), _issues.Count == 0); }
    }
}
