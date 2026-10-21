using System.ComponentModel;
using System.Text;
using WebClient = LakeCode.Core.Agent.Tools.Web.WebClient;

namespace LakeCode.Core.Agent.Tools.ReadWeb;

internal sealed class ReadWebTool(WebClient web)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(ReadAsync, name: "read_web");

    private const string Truncated = "[Output truncated; request a narrower line range.]";
    private const int MarkerReserve = 100;

    [Description("Read extracted text of a public HTTP(S) page through Parallel then Exa, with line numbers. External services receive the URL. Output is limited to 32000 characters and extraction to 500000 characters. No local files, authenticated pages, or browser interaction. Each call extracts again; ranges can differ if the page changes.")]
    public Task<string> ReadAsync(
        [Description("Public HTTP(S) URL with a DNS hostname and standard port; no userinfo, IP literals, or internal hosts.")] string url,
        [Description("First content line, numbered from 1; metadata is not numbered.")] int startLine = 1,
        [Description("Maximum lines to read; 0 means all remaining lines up to the output limit.")] int lineCount = 200,
        CancellationToken cancellationToken = default) => WorkspaceFiles.ExecuteAsync(async () =>
    {
        if (startLine < 1 || lineCount < 0) throw new ArgumentException("startLine must be positive and lineCount nonnegative.");
        var uri = WebClient.ParsePublicUrl(url ?? "");
        var page = await web.ReadAsync(uri, cancellationToken);
        var output = new StringBuilder().Append("Provider: ").AppendLine(page.Provider)
            .Append("Source URL: ").AppendLine(page.Url);
        // Metadata shares the output budget; a provider title must not consume the whole range.
        if (page.Title is { } title) output.Append("Title: ").Append(title.AsSpan(0, SafeLength(title, 300))).Append('\n');
        foreach (var note in page.Notes) output.Append("Note: ").AppendLine(note);
        output.AppendLine("Extracted web content; page sections may be omitted by the provider.");
        if (page.ContentLimited) output.AppendLine("[Provider extraction limited to 500000 characters; more page content may be unavailable.]");
        var budget = WorkspaceFiles.MaxOutputCharacters - MarkerReserve;
        using var reader = new StringReader(page.Content);
        var number = 0;
        var emitted = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            if (number < startLine) continue;
            if (lineCount != 0 && emitted >= lineCount)
                return output.Append($"[More lines remain; call read_web with startLine={number}.]").ToString();
            var prefix = $"{number}: ";
            if (output.Length + prefix.Length + line.Length + 1 > budget)
            {
                var available = Math.Max(0, budget - output.Length - prefix.Length - 1);
                if (output.Length + prefix.Length + 1 <= budget)
                    output.Append(prefix).Append(line.AsSpan(0, SafeLength(line, available))).Append('\n');
                return output.Append(Truncated).ToString();
            }
            output.Append(prefix).Append(line).Append('\n');
            emitted++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.Append(emitted == 0 ? "No lines in the requested range." : "[End of extracted content.]").ToString();
    });

    private static int SafeLength(string value, int maximum)
    {
        var length = Math.Min(value.Length, maximum);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return length;
    }
}
