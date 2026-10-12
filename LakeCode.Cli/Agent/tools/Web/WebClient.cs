using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LakeCode.Cli.Agent.Tools.Web;

internal sealed record WebSource(string Title, string Url, string? Snippet, string? PublishedDate);
internal sealed record WebSearchResult(string Provider, WebSource[] Sources, string[] Notes);
internal sealed record WebPageResult(string Provider, string Url, string? Title, string Content, bool ContentLimited, string[] Notes);

internal sealed class WebClient(string sessionId)
{
    private const int MaxContent = 500000;
    private static readonly Uri ParallelMcp = new("https://search.parallel.ai/mcp");
    private static readonly Uri ExaMcp = new("https://mcp.exa.ai/mcp?tools=web_search_exa,web_fetch_exa");
    private readonly string? _parallelKey = ReadKey("PARALLEL_API_KEY");
    private readonly string? _exaKey = ReadKey("EXA_API_KEY");

    private static string? ReadKey(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal async Task<WebSearchResult> SearchAsync(string query, int limit, DateOnly? afterDate, CancellationToken cancellationToken)
    {
        var failures = new string[2];
        for (var candidate = 0; candidate < 2; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var key = Key(candidate);
                var payload = await SearchProviderAsync(candidate, key, query, limit, afterDate, deadline.Token);
                var sources = NormalizeSources(candidate, payload, key is null)
                    .DistinctBy(source => source.Url, StringComparer.Ordinal).Take(limit).ToArray();
                deadline.Token.ThrowIfCancellationRequested();
                if (sources.Length == 0) throw new IOException("No usable results.");
                cancellationToken.ThrowIfCancellationRequested();
                return new WebSearchResult(Provider(candidate, key), sources, Notes(candidate, failures));
            }
            catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
            {
                failures[candidate] = FailureReason(exception);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new IOException($"Web search failed. Parallel: {failures[0]}; Exa: {failures[1]}.");
    }

    internal async Task<WebPageResult> ReadAsync(Uri url, CancellationToken cancellationToken)
    {
        var failures = new string[2];
        for (var candidate = 0; candidate < 2; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var key = Key(candidate);
                var payload = await ReadProviderAsync(candidate, key, url, deadline.Token);
                var page = NormalizePage(candidate, payload, key is null, url);
                deadline.Token.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                return page with { Provider = Provider(candidate, key), Notes = Notes(candidate, failures) };
            }
            catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
            {
                failures[candidate] = FailureReason(exception);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new IOException($"Web page read failed. Parallel: {failures[0]}; Exa: {failures[1]}.");
    }

    private string? Key(int candidate)
    {
        var key = candidate == 0 ? _parallelKey : _exaKey;
        if (key is not null && (key.Contains('\r') || key.Contains('\n') || key.Any(char.IsControl)))
            throw new IOException("Invalid API key format.");
        return key?.Trim();
    }
    private static bool IsProviderFailure(Exception exception, CancellationToken caller) =>
        exception is HttpRequestException or IOException or JsonException || exception is OperationCanceledException && !caller.IsCancellationRequested;
    private static string FailureReason(Exception exception) => exception switch
    {
        HttpRequestException => "Network request failed.",
        JsonException => "Invalid provider response.",
        OperationCanceledException => "Timed out after 60 seconds.",
        IOException { Message: "Invalid API key format." or "No usable results." or "No readable page content."
            or "Unsupported Parallel search response." or "Unsupported Exa search response." or "Response exceeded 4 MiB."
            or "Unsupported web service endpoint." or "Unsupported web service response type." or "Missing MCP response."
            or "Invalid MCP envelope." or "Invalid MCP error." or "Invalid MCP tool result." or "MCP tool returned an error." } => exception.Message,
        IOException when IsNumericFailure(exception.Message, "HTTP ") || IsNumericFailure(exception.Message, "MCP error ") => exception.Message,
        _ => "Network request failed."
    };
    private static bool IsNumericFailure(string message, string prefix) => message.StartsWith(prefix, StringComparison.Ordinal)
        && message.EndsWith('.') && int.TryParse(message.AsSpan(prefix.Length, message.Length - prefix.Length - 1), out _);
    private static string Provider(int candidate, string? key) => $"{(candidate == 0 ? "Parallel" : "Exa")} ({(key is null ? "MCP" : "REST")})";
    private static string[] Notes(int candidate, string[] failures) => candidate == 0 ? [] : [$"Parallel failed: {failures[0]}; used Exa."];

    private Task<JsonElement> SearchProviderAsync(int candidate, string? key, string query, int limit, DateOnly? afterDate, CancellationToken token)
    {
        var date = afterDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (key is null)
        {
            var effectiveQuery = date is null ? query : $"{query} after:{date}";
            return candidate == 0
                ? WebTransport.CallMcpToolAsync(ParallelMcp, "web_search", new { objective = effectiveQuery, search_queries = new[] { effectiveQuery }, session_id = sessionId }, token)
                : WebTransport.CallMcpToolAsync(ExaMcp, "web_search_exa", new { query = effectiveQuery, numResults = limit }, token);
        }
        if (candidate == 0)
        {
            var advanced = new Dictionary<string, object> { ["max_results"] = limit };
            if (date is not null) advanced["source_policy"] = new { after_date = date };
            return WebTransport.PostJsonAsync(new Uri("https://api.parallel.ai/v1/search"),
                new { objective = query, search_queries = new[] { query }, mode = "fast", max_chars_total = 24000, session_id = sessionId, advanced_settings = advanced }, key, token);
        }
        var body = new Dictionary<string, object> { ["query"] = query, ["numResults"] = limit, ["type"] = "auto", ["contents"] = new { highlights = true } };
        if (date is not null) body["startPublishedDate"] = $"{date}T00:00:00Z";
        return WebTransport.PostJsonAsync(new Uri("https://api.exa.ai/search"), body, key, token);
    }

    private Task<JsonElement> ReadProviderAsync(int candidate, string? key, Uri url, CancellationToken token)
    {
        if (key is null)
            return candidate == 0
                ? WebTransport.CallMcpToolAsync(ParallelMcp, "web_fetch", new { urls = new[] { url.AbsoluteUri }, full_content = true, session_id = sessionId }, token)
                : WebTransport.CallMcpToolAsync(ExaMcp, "web_fetch_exa", new { urls = new[] { url.AbsoluteUri }, maxCharacters = MaxContent }, token);
        return candidate == 0
            ? WebTransport.PostJsonAsync(new Uri("https://api.parallel.ai/v1/extract"), new { urls = new[] { url.AbsoluteUri }, session_id = sessionId, advanced_settings = new { full_content = true } }, key, token)
            : WebTransport.PostJsonAsync(new Uri("https://api.exa.ai/contents"), new { urls = new[] { url.AbsoluteUri }, text = new { maxCharacters = MaxContent } }, key, token);
    }

    private static bool Results(JsonElement payload, out JsonElement results)
    {
        results = default;
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("results", out results) && results.ValueKind == JsonValueKind.Array;
    }
    private static JsonElement? StructuredResults(JsonElement payload, bool mcp)
    {
        if (!mcp) return Results(payload, out var direct) ? direct : null;
        if (payload.TryGetProperty("structuredContent", out var structured) && Results(structured, out var results)) return results;
        foreach (var text in TextBlocks(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (Results(document.RootElement, out results)) return results.Clone();
            }
            catch (JsonException) { /* Text MCP results need not be JSON. */ }
        }
        return null;
    }
    private static IEnumerable<string> TextBlocks(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
            if (Text(block, "type") == "text" && Text(block, "text") is { } text) yield return text;
    }
    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() : null;
    private static string? Excerpts(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        var texts = array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()).Where(text => !string.IsNullOrWhiteSpace(text));
        var joined = string.Join("\n\n", texts);
        return joined.Length == 0 ? null : joined;
    }

    private static IEnumerable<WebSource> NormalizeSources(int candidate, JsonElement payload, bool mcp)
    {
        if (StructuredResults(payload, mcp) is { } results)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (!TryPublicUrl(Text(item, "url"), out var url)) continue;
                var normalized = new UriBuilder(url!) { Fragment = "" }.Uri.AbsoluteUri;
                var snippet = candidate == 0 ? Excerpts(item, "excerpts") : Excerpts(item, "highlights") ?? NonBlank(Text(item, "text")) ?? NonBlank(Text(item, "summary"));
                yield return new WebSource(NonBlank(Text(item, "title")) ?? normalized, normalized, snippet,
                    NonBlank(Text(item, candidate == 0 ? "publish_date" : "publishedDate")));
            }
            yield break;
        }
        if (candidate == 0 || !mcp) throw new IOException(candidate == 0 ? "Unsupported Parallel search response." : "Unsupported Exa search response.");
        foreach (var text in TextBlocks(payload))
            foreach (var source in ParseExaSearch(text)) yield return source;
    }

    private static IEnumerable<WebSource> ParseExaSearch(string text)
    {
        if (text.Trim() == "No search results found. Please try a different query.") yield break;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsSourceStart(lines, i)) continue;
            var title = lines[i][7..];
            var urlText = lines[++i][5..];
            string? date = null;
            var snippet = new StringBuilder();
            var inContent = false;
            for (i++; i < lines.Length && !IsSourceStart(lines, i); i++)
            {
                var line = lines[i];
                if (!inContent && line.StartsWith("Published: ", StringComparison.Ordinal)) { date = NonBlank(line[11..]); continue; }
                if (!inContent && line.StartsWith("Author: ", StringComparison.Ordinal)) continue;
                if (!inContent && (line.StartsWith("Highlights:", StringComparison.Ordinal) || line.StartsWith("Text:", StringComparison.Ordinal)))
                {
                    inContent = true;
                    line = line[(line.IndexOf(':') + 1)..].TrimStart();
                }
                if (inContent) snippet.AppendLine(line);
            }
            i--;
            if (!TryPublicUrl(urlText, out var url)) continue;
            var normalized = new UriBuilder(url!) { Fragment = "" }.Uri.AbsoluteUri;
            yield return new WebSource(NonBlank(title) ?? normalized, normalized, NonBlank(snippet.ToString().Trim()), date);
        }
    }
    private static bool IsSourceStart(string[] lines, int index) => index + 1 < lines.Length
        && lines[index].StartsWith("Title: ", StringComparison.Ordinal) && lines[index + 1].StartsWith("URL: ", StringComparison.Ordinal);

    private static WebPageResult NormalizePage(int candidate, JsonElement payload, bool mcp, Uri requested)
    {
        if (StructuredResults(payload, mcp) is { } results)
        {
            foreach (var item in results.EnumerateArray())
            {
                var content = NonBlank(Text(item, candidate == 0 ? "full_content" : "text"));
                if (content is null) continue;
                var validUrl = TryPublicUrl(Text(item, "url"), out var url);
                if (candidate == 0 && !validUrl) continue;
                return Page(validUrl ? url!.AbsoluteUri : requested.AbsoluteUri, NonBlank(Text(item, "title")), content);
            }
            throw new IOException("No readable page content.");
        }
        if (candidate == 1 && mcp)
        {
            foreach (var text in TextBlocks(payload))
            {
                using var reader = new StringReader(text);
                var first = reader.ReadLine();
                var second = reader.ReadLine();
                if (first is null || !first.StartsWith("# ", StringComparison.Ordinal) || second is null || !second.StartsWith("URL: ", StringComparison.Ordinal)) continue;
                string? line;
                var valid = true;
                while ((line = reader.ReadLine()) is not null && line.Length != 0)
                    if (!line.StartsWith("Published:", StringComparison.Ordinal) && !line.StartsWith("Author:", StringComparison.Ordinal)) valid = false;
                if (!valid || line is null) continue;
                var body = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(body) || body.Trim() is "No content found." or "No content found for the provided URL(s).") continue;
                return Page(TryPublicUrl(second[5..], out var url) ? url!.AbsoluteUri : requested.AbsoluteUri, NonBlank(first[2..]), body);
            }
        }
        throw new IOException("No readable page content.");
    }
    private static WebPageResult Page(string url, string? title, string content)
    {
        var length = Math.Min(content.Length, MaxContent);
        if (length < content.Length && length > 0 && char.IsHighSurrogate(content[length - 1]) && char.IsLowSurrogate(content[length])) length--;
        return new WebPageResult("", url, title, content[..length], content.Length >= MaxContent, []);
    }
    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "N/A" ? null : value;
    private static bool TryPublicUrl(string? value, out Uri? url)
    {
        try { url = ParsePublicUrl(value ?? ""); return true; }
        catch (ArgumentException) { url = null; return false; }
    }
    internal static Uri ParsePublicUrl(string url)
    {
        if (url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort
            && uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback)
        {
            var host = uri.IdnHost.TrimEnd('.');
            if (host.Contains('.') && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return uri;
        }
        throw new ArgumentException("read_web accepts public HTTP(S) URLs with a DNS hostname only.");
    }
}
