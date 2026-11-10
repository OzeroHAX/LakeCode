using System.Text;
using LakeCode.Agent.Tools.Files;
using LakeCode.Search;

namespace LakeCode.Agent.Tools;

/// <summary>Bounds result text so one large match set cannot exhaust the model context.</summary>
internal sealed class SearchOutput(WorkspaceSearchSource source)
{
    private const string TruncatedMarker =
        "\n[Output truncated; narrow the scope or request fewer items. Search output is incomplete.]";

    private readonly StringBuilder _text = new();
    private bool _truncated;

    public void Add(string line)
    {
        if (_truncated)
        {
            return;
        }

        var remaining = WorkspaceFiles.MaxOutputCharacters - TruncatedMarker.Length - _text.Length;

        if (line.Length + 1 > remaining)
        {
            _text.Append(line.AsSpan(0, Math.Max(0, remaining))).Append(TruncatedMarker);
            _truncated = true;
        }
        else
        {
            _text.Append(line).Append('\n');
        }
    }

    public void Issues(IReadOnlyList<SearchIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (issues.Count == 0)
        {
            return;
        }

        Add($"[Search incomplete: {issues.Count} skipped files.]");

        foreach (var issue in issues)
        {
            Add($"  skipped {source.Display(issue.Path)}: {issue.Reason}");
        }
    }

    public void Outline(FileOutline outline, int max)
    {
        ArgumentNullException.ThrowIfNull(outline);

        Add(
            $"  language: {outline.Language ?? "unsupported (text search only)"}; " +
            $"lines: {outline.LineCount}; syntax errors: {outline.HasSyntaxErrors}");

        foreach (var item in outline.Items.Take(max))
        {
            Add($"    {item.Kind} {item.Label} @{item.StartLine}-{item.EndLine}");
        }

        if (outline.Items.Count > max)
        {
            Add($"    [{outline.Items.Count - max} structure items omitted]");
        }
    }

    public void Ranked(RankedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        Add(
            $"{source.Display(file.Path)} " +
            $"(score {file.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

        foreach (var reason in file.Reasons)
        {
            Add("  why: " + reason);
        }

        Outline(file.Outline, 8);
    }

    public override string ToString() => _text.ToString();
}

/// <summary>Shared engine construction and error translation for the search capabilities.</summary>
internal static class SearchSupport
{
    public static SearchEngine Engine(WorkspaceSearchSource source, int maxResults = 1000)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SearchEngine(source, new SearchLimits
        {
            MaxFileBytes = WorkspaceFiles.MaxFileBytes,
            MaxResults = maxResults,
        });
    }

    public static SearchFilter Filter(string glob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);

        return new SearchFilter { Include = [glob] };
    }

    public static void ValidateTop(int top)
    {
        if (top is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(top));
        }
    }

    /// <summary>Search failures are reported as tool errors instead of failing the whole run.</summary>
    public static async ValueTask<AgentToolResult> RunAsync(Func<Task<string>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return new AgentToolResult(false, await action().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SearchLimitException error)
        {
            return new AgentToolResult(true, "Error: " + error.Message);
        }
        catch (ToolArgumentException error)
        {
            return new AgentToolResult(true, "Error: " + error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or InvalidOperationException or NotSupportedException
                                          or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new AgentToolResult(true, "Error: Search/structure analysis failed: " + error.Message);
        }
    }
}
