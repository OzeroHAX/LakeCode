namespace LakeCode.Search;

/// <summary>The host owns enumeration, authorization and opening streams. Enumerated identifiers must be unique relative paths using '/' separators.</summary>
public interface ISearchSource
{
    IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken);
    /// <summary>Returns a readable stream owned and disposed by the search operation.</summary>
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
}

public sealed record SearchLimits
{
    public int MaxFileBytes { get; init; } = 1_048_576;
    public long MaxTotalBytes { get; init; } = 64 * 1_048_576;
    public int MaxFiles { get; init; } = 100_000;
    public int MaxNodes { get; init; } = 100_000;
    public int MaxResults { get; init; } = 1_000;
    public int MaxLineCharacters { get; init; } = 32_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record SearchFilter
{
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

public sealed record GrepRequest(string Pattern)
{
    public bool Regex { get; init; }
    public bool IgnoreCase { get; init; }
    public SearchFilter Filter { get; init; } = new();
}
public sealed record FindRequest(string Query)
{
    public int Top { get; init; } = 10;
    public SearchFilter Filter { get; init; } = new();
}
public enum TraceRelation { Defined, CalledFrom, TriggeredFrom, Rendered, Populated, ComesFrom, Handled, Implementation }
public sealed record TraceRequest(string Subject, TraceRelation Relation)
{
    public IReadOnlyList<string> Support { get; init; } = [];
    public SearchFilter Filter { get; init; } = new();
    public int Top { get; init; } = 10;
    public int ContextLines { get; init; } = 3;
    public int FullRegionLines { get; init; } = 40;
}
public enum DuplicateUnitKind { Any, Callable, Type, Declaration, Block }
public enum DuplicateComparison { Exact, Normalized }
public sealed record DuplicateRequest
{
    public SearchFilter Filter { get; init; } = new();
    public DuplicateUnitKind UnitKind { get; init; } = DuplicateUnitKind.Callable;
    public DuplicateComparison Comparison { get; init; } = DuplicateComparison.Normalized;
    public bool CrossLanguage { get; init; }
    public int MinLines { get; init; } = 3;
    public int Top { get; init; } = 50;
}
public sealed record StructureItem(string Kind, string Label, int StartLine, int EndLine, int Depth);
public sealed record DuplicateUnit(string Path, string? Language, string Kind, string Label, int StartLine, int EndLine);
public sealed record DuplicateGroup(string Key, string? Language, string Kind, double Similarity, IReadOnlyList<DuplicateUnit> Units);
public sealed record FileOutline(string Path, string? Language, int LineCount, bool HasSyntaxErrors,
    IReadOnlyList<StructureItem> Items);
public sealed record SearchIssue(string Path, string Reason);
public sealed record TextMatch(int Line, string Text, StructureItem? Enclosing);
public sealed record GrepFile(FileOutline Outline, IReadOnlyList<TextMatch> Matches);
public sealed record RankedFile(string Path, double Score, IReadOnlyList<string> Reasons, FileOutline Outline);
public sealed record CodeRegion(int StartLine, int EndLine, string Text, StructureItem? Structure);
public sealed record TraceFile(RankedFile File, IReadOnlyList<CodeRegion> Regions);
/// <summary>Complete is false only for reported skipped source files. Budget failures throw instead of returning partial success.</summary>
public sealed record SearchResult<T>(IReadOnlyList<T> Items, IReadOnlyList<SearchIssue> Issues, bool Complete);
public sealed class SearchLimitException(string message) : Exception(message);

public sealed record Grammar(string Id, IReadOnlyList<string> Extensions);
