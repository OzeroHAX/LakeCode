using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LakeCode.Agent.Tools.Files;
using LakeCode.Agent.Tools.Web;

namespace LakeCode.Agent.Tools;

/// <summary>Searches public sources and returns URLs with excerpts.</summary>
public sealed class WebSearchTool(WebClient web) : AgentTool
{
    private const string FilterNotice =
        "Search operators and date filters are provider-dependent hints, not strict constraints.";

    private const string Truncated = "[Output truncated; narrow the query or reduce limit.]";

    private static readonly Regex DateDirective = new(
        @"(?<!\S)(?:after|before):(?<date>\d{4}-\d{2}-\d{2})(?=\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    public override string Name => "web_search";

    public override string Description =>
        "Search public web sources through Parallel then Exa. Queries are sent to external services. " +
        "Returns source URLs and excerpts, not a verified answer. Output is limited to 32000 characters.";

    public override AgentToolMetadata Metadata { get; } =
        new(AgentToolEffects.CallsExternalService);

    public override System.Text.Json.JsonElement? ParametersSchema => ToolSchema.Object(schema => schema
        .Text("query", "Search query, 1 to 4000 characters; operators are provider-dependent hints.")
        .Integer("limit", "Maximum source count, 1 to 10.", minimum: 1, maximum: 10, defaultValue: 5)
        .Text("recency", "Optional freshness hint: day, week, month, year. Explicit valid after:/before: date tokens take precedence."));

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        ToolArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var query = (arguments.RequireString("query", allowEmpty: true) ?? string.Empty).Trim();
        var limit = arguments.Integer("limit", 5);
        var recency = arguments.OptionalString("recency");

        return GuardAsync(async () =>
        {
            if (query.Length is < 1 or > 4000)
            {
                throw new ArgumentException("query must contain 1 to 4000 characters.");
            }

            if (limit is < 1 or > 10)
            {
                throw new ArgumentException("limit must be between 1 and 10.");
            }

            var window = string.IsNullOrWhiteSpace(recency) ? null : recency.Trim().ToLowerInvariant();

            var days = window switch
            {
                null => 0,
                "day" => 1,
                "week" => 7,
                "month" => 30,
                "year" => 365,
                _ => throw new ArgumentException("recency must be day, week, month, or year."),
            };

            DateOnly? afterDate = null;

            if (days != 0 && !HasExplicitDate(query))
            {
                afterDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime);
            }

            var result = await web.SearchAsync(query, limit, afterDate, cancellationToken).ConfigureAwait(false);

            var output = new StringBuilder()
                .Append("Provider: ")
                .AppendLine(result.Provider)
                .AppendLine(FilterNotice);

            foreach (var note in result.Notes)
            {
                output.Append("Note: ").AppendLine(note);
            }

            var budget = WorkspaceFiles.MaxOutputCharacters - Truncated.Length;
            var truncated = false;

            for (var i = 0; i < result.Sources.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = result.Sources[i];
                var title = source.Title.AsSpan(0, SafeLength(source.Title, 300));
                var prefix = $"\n[{i + 1}] ";

                if (output.Length + prefix.Length + title.Length + source.Url.Length + 2 > budget)
                {
                    truncated = true;
                    break;
                }

                output.Append(prefix).Append(title).Append('\n').Append(source.Url).Append('\n');

                if (source.PublishedDate is { } date)
                {
                    if (output.Length + date.Length + 12 <= budget)
                    {
                        output.Append("Published: ").Append(date).Append('\n');
                    }
                    else
                    {
                        truncated = true;
                    }
                }

                if (source.Snippet is { } snippet)
                {
                    var length = SafeLength(snippet, Math.Min(1500, Math.Max(0, budget - output.Length - 1)));

                    if (length > 0)
                    {
                        output.Append(snippet.AsSpan(0, length)).Append('\n');
                    }

                    if (length < snippet.Length)
                    {
                        truncated = true;
                    }
                }
            }

            if (truncated)
            {
                output.Append(Truncated);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return Success(output.ToString());
        });
    }

    private static bool HasExplicitDate(string query)
    {
        foreach (Match match in DateDirective.Matches(query))
        {
            // A whitespace token can still occur inside a quoted phrase.
            var quoted = false;

            for (var i = 0; i < match.Index; i++)
            {
                if (query[i] == '"')
                {
                    quoted = !quoted;
                }
            }

            if (!quoted && DateOnly.TryParseExact(
                    match.Groups["date"].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private static int SafeLength(string value, int maximum)
    {
        var length = Math.Min(value.Length, maximum);

        if (length > 0 && length < value.Length &&
            char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return length;
    }
}
