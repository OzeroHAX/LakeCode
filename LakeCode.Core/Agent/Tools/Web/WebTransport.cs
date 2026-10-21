using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;

namespace LakeCode.Core.Agent.Tools.Web;

internal static class WebTransport
{
    private const long MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static Task<JsonElement> PostJsonAsync(Uri endpoint, object body, string apiKey,
        CancellationToken cancellationToken) => SendAsync(endpoint, body, apiKey, null, cancellationToken);

    internal static Task<JsonElement> CallMcpToolAsync(Uri endpoint, string toolName, object arguments,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        return SendAsync(endpoint, new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = toolName, arguments } },
            null, id, cancellationToken);
    }

    private static async Task<JsonElement> SendAsync(Uri endpoint, object body, string? apiKey, string? id,
        CancellationToken cancellationToken)
    {
        if (endpoint.AbsoluteUri is not ("https://api.parallel.ai/v1/search" or "https://api.parallel.ai/v1/extract"
            or "https://api.exa.ai/search" or "https://api.exa.ai/contents" or "https://search.parallel.ai/mcp"
            or "https://mcp.exa.ai/mcp?tools=web_search_exa,web_fetch_exa"))
            throw new IOException("Unsupported web service endpoint.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd(id is null ? "application/json" : "application/json, text/event-stream");
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new IOException($"HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new IOException("Response exceeded 4 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var limited = new LimitedReadStream(stream);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "application/json")
        {
            using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
            if (id is null) return document.RootElement.Clone();
            if (FindResponse(document.RootElement, id) is { } result) return result;
        }
        else if (id is not null && mediaType == "text/event-stream")
        {
            await foreach (var item in SseParser.Create(limited).EnumerateAsync(cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(item.Data)) continue;
                if (item.Data.Trim() == "[DONE]") break;
                using var document = JsonDocument.Parse(item.Data);
                if (FindResponse(document.RootElement, id) is { } result) return result;
            }
        }
        else throw new IOException("Unsupported web service response type.");
        throw new IOException("Missing MCP response.");
    }

    private static JsonElement? FindResponse(JsonElement envelope, string id)
    {
        if (envelope.ValueKind == JsonValueKind.Array)
        {
            JsonElement? match = null;
            foreach (var item in envelope.EnumerateArray())
            {
                var result = FindResponse(item, id);
                if (result is not null) match = result;
            }
            return match;
        }
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            throw new IOException("Invalid MCP envelope.");
        if (envelope.TryGetProperty("method", out var method))
        {
            if (method.ValueKind != JsonValueKind.String || envelope.TryGetProperty("result", out _) || envelope.TryGetProperty("error", out _))
                throw new IOException("Invalid MCP envelope.");
            return null;
        }
        if (!envelope.TryGetProperty("id", out var responseId)) throw new IOException("Invalid MCP envelope.");
        var hasResult = envelope.TryGetProperty("result", out var resultValue);
        var hasError = envelope.TryGetProperty("error", out var error);
        if (hasResult == hasError) throw new IOException("Invalid MCP envelope.");
        if (responseId.ValueKind != JsonValueKind.String || responseId.GetString() != id) return null;
        if (hasError)
        {
            if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number))
                throw new IOException("Invalid MCP error.");
            throw new IOException($"MCP error {number}.");
        }
        if (resultValue.ValueKind != JsonValueKind.Object) throw new IOException("Invalid MCP tool result.");
        if (resultValue.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new IOException("MCP tool returned an error.");
        return resultValue.Clone();
    }

    private sealed class LimitedReadStream(Stream inner) : Stream
    {
        private long _read;
        private int Count(int count)
        {
            _read += count;
            if (_read > MaxResponseBytes) throw new IOException("Response exceeded 4 MiB.");
            return count;
        }
        private int Allowance(int count) => (int)Math.Min(count, MaxResponseBytes - _read + 1);
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allowance(count)));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allowance(buffer.Length)]));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, Allowance(count)), cancellationToken));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Allowance(buffer.Length)], cancellationToken));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
