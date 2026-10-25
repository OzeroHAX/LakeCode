using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace LakeCode.Core.Mcp;

public enum McpTransport { Stdio, Http }
public enum McpApprovalPolicy { Ask, Allow }

/// <summary>User-configured connection. Never serialize this record into conversation state.</summary>
public sealed record McpServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Revision { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public McpTransport Transport { get; init; }
    public string Command { get; init; } = "";
    public ImmutableArray<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public string Url { get; init; } = "";
    public ImmutableDictionary<string, string> Headers { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;
    public McpApprovalPolicy Approval { get; init; } = McpApprovalPolicy.Ask;

    public void Validate()
    {
        if (!Guid.TryParse(Id, out _) || !Guid.TryParse(Revision, out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 ||
            Name.Any(char.IsControl) || !Enum.IsDefined(Transport) || !Enum.IsDefined(Approval) ||
            Command is null || WorkingDirectory is null || Url is null ||
            Arguments.IsDefault || Arguments.Any(a => a is null || a.Contains('\0')) || Headers is null || Environment is null)
            throw new InvalidDataException("Некорректная конфигурация MCP-сервера.");
        if (Transport == McpTransport.Stdio && (string.IsNullOrWhiteSpace(Command) || Command.Contains('\0') ||
            (WorkingDirectory.Length > 0 && !Path.IsPathFullyQualified(WorkingDirectory))))
            throw new InvalidDataException("Для stdio укажите команду; рабочая папка должна быть абсолютной.");
        if (Transport == McpTransport.Http && (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
            throw new InvalidDataException("Для HTTP укажите абсолютный http(s) URL без userinfo и фрагмента.");
        foreach (var (key, value) in Headers)
            if (string.IsNullOrWhiteSpace(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
                value is null || value.Any(c => c is '\r' or '\n' or '\0') ||
                key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Некорректный HTTP-заголовок MCP; Host и Mcp-* задаются транспортом.");
        foreach (var (key, value) in Environment)
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0') || value is null || value.Contains('\0'))
                throw new InvalidDataException("Некорректная переменная окружения MCP.");
    }
}

/// <summary>Non-secret references kept in an accepted queue item.</summary>
public sealed record McpServerSelection(string Id, string Revision);

internal sealed record McpRequestConfiguration(ImmutableArray<McpServerProfile> Servers, string? Error = null)
{
    public static readonly McpRequestConfiguration Empty = new([]);
    public McpServerSelection[] Selection => Servers.Select(s => new McpServerSelection(s.Id, s.Revision)).ToArray();
}
