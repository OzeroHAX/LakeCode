using System.Text.Json;

namespace LakeCode.Core.Settings;

/// <summary>Global agent preferences, separate from connection profiles and session history.</summary>
public sealed class AgentSettingsStorage
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; }
    private AgentSettings _current = new();
    public AgentSettings Current => Volatile.Read(ref _current);

    public AgentSettingsStorage()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new IOException("Не удалось определить домашний каталог.");
        FilePath = Path.Combine(home, ".lakecode", "settings.json");
    }

    public async Task<AgentSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        ValidatePath();
        if (!File.Exists(FilePath)) return new AgentSettings();
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxBytes) throw new InvalidDataException("settings.json превышает 1 MiB.");
        AgentSettings settings;
        try
        {
            settings = await JsonSerializer.DeserializeAsync<AgentSettings>(stream, Options, cancellationToken)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Повреждён settings.json. Исправьте файл; настройки не были сброшены.");
        }
        settings.Validate();
        Volatile.Write(ref _current, settings);
        return settings;
    }

    public async Task SaveAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        ValidatePath();
        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("settings.json превышает 1 MiB.");
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            ValidatePath();
            if (!OperatingSystem.IsWindows() && File.Exists(FilePath))
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(FilePath));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
            Volatile.Write(ref _current, settings);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ValidatePath()
    {
        for (string? current = FilePath; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("Символические ссылки в пути settings.json запрещены.");
    }
}
