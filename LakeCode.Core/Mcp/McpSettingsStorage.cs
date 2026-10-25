using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LakeCode.Core.Mcp;

/// <summary>Atomic, user-only storage; reading or saving never contacts or starts a server.</summary>
public sealed class McpSettingsStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private sealed record Document(int SchemaVersion, ImmutableArray<McpServerProfile> Servers);
    private sealed record Snapshot(ImmutableArray<McpServerProfile> Servers);
    private Snapshot _current = new([]);
    private readonly SemaphoreSlim _writes = new(1);
    public ImmutableArray<McpServerProfile> Servers => Volatile.Read(ref _current).Servers;
    public string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lakecode", "mcp.json");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ValidatePath();
        if (!File.Exists(FilePath)) return;
        ValidatePermissions();
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("mcp.json превышает 1 MiB.");
        Document document;
        try { document = await JsonSerializer.DeserializeAsync<Document>(stream, Options, cancellationToken) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("Повреждён mcp.json; конфигурация не сброшена."); }
        if (document.SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая версия mcp.json.");
        Validate(document.Servers);
        Volatile.Write(ref _current, new(document.Servers));
    }

    public async Task SaveServerAsync(McpServerProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        await _writes.WaitAsync(cancellationToken);
        try
        {
            var next = Servers.Where(s => s.Id != profile.Id).Append(profile with { Revision = Guid.NewGuid().ToString("N") }).ToImmutableArray();
            await SaveAsync(next, cancellationToken);
        }
        finally { _writes.Release(); }
    }

    public async Task DeleteServerAsync(string id, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try { await SaveAsync(Servers.Where(s => s.Id != id).ToImmutableArray(), cancellationToken); }
        finally { _writes.Release(); }
    }

    internal McpRequestConfiguration Capture() => new(Servers.Where(s => s.Enabled).ToImmutableArray());
    internal McpRequestConfiguration Resolve(McpServerSelection[]? selection)
    {
        if (selection is null) return McpRequestConfiguration.Empty;
        var result = ImmutableArray.CreateBuilder<McpServerProfile>();
        foreach (var item in selection)
        {
            var profile = Servers.FirstOrDefault(s => s.Id == item.Id && s.Revision == item.Revision && s.Enabled);
            if (profile is null) return new([], "Конфигурация MCP ожидающего сообщения изменилась или удалена. Отправьте сообщение заново.");
            result.Add(profile);
        }
        return new(result.ToImmutable());
    }

    private async Task SaveAsync(ImmutableArray<McpServerProfile> servers, CancellationToken cancellationToken)
    {
        Validate(servers);
        ValidatePath();
        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, servers), Options);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("mcp.json превышает 1 MiB.");
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            ValidatePath();
            if (!OperatingSystem.IsWindows() && File.Exists(FilePath))
            {
                var permissions = File.GetUnixFileMode(FilePath);
                if ((permissions & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    throw new IOException("mcp.json содержит секреты: установите права 600 перед сохранением.");
                File.SetUnixFileMode(temporary, permissions);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, true);
            Volatile.Write(ref _current, new(servers));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(ImmutableArray<McpServerProfile> servers)
    {
        if (servers.IsDefault || servers.Length > 100 || servers.Any(s => s is null) || servers.Select(s => s.Id).Distinct().Count() != servers.Length)
            throw new InvalidDataException("Некорректный список MCP-серверов.");
        foreach (var server in servers) server.Validate();
        if (servers.Select(s => Guid.Parse(s.Id)).Distinct().Count() != servers.Length)
            throw new InvalidDataException("MCP-серверы должны иметь разные ID.");
    }

    private void ValidatePermissions()
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(FilePath) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("mcp.json содержит секреты: установите права 600.");
    }

    private void ValidatePath()
    {
        for (string? path = FilePath; path is not null; path = Path.GetDirectoryName(path))
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException("Символические ссылки в пути mcp.json запрещены.");
    }
}
