using System.Text.Json;
using System.Text.Json.Nodes;

namespace LakeCode.Core.Mcp;

/// <summary>Prevent configured headers/env from being echoed into tool results and persisted history.</summary>
internal static class McpRedaction
{
    public static string Text(McpServerProfile profile, string value)
    {
        // Ordinary env (for example DEBUG=1) and content negotiation headers are not credentials.
        // Treat custom headers as sensitive, and recognize conventional credential env names.
        var headers = profile.Headers.Where(p => !new[] { "Accept", "Content-Type", "User-Agent", "Accept-Language" }
            .Contains(p.Key, StringComparer.OrdinalIgnoreCase));
        var secrets = headers.Select(p => p.Value).Concat(headers
                .Where(p => p.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.Split(' ', 2)).Where(parts => parts.Length == 2).Select(parts => parts[1]))
            .Concat(profile.Environment.Where(p => IsCredentialName(p.Key)).Select(p => p.Value));
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length))
            value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value;
    }

    private static bool IsCredentialName(string name) =>
        new[] { "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "API_KEY", "APIKEY", "PRIVATE_KEY", "AUTH" }
            .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static string Json(McpServerProfile profile, string json)
    {
        var node = JsonNode.Parse(json);
        Scrub(node);
        return node?.ToJsonString() ?? "null";

        void Scrub(JsonNode? current)
        {
            if (current is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text)) obj[pair.Key] = Text(profile, text);
                    else Scrub(pair.Value);
                }
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var text)) array[i] = Text(profile, text);
                    else Scrub(array[i]);
                }
        }
    }
}
