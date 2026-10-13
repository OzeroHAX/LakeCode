using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LakeCode.Core.Providers;

/// <summary>User-wide credentials, never stored in a workspace or a conversation snapshot.</summary>
public sealed class ProviderStorage
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public string FilePath { get; }

    public ProviderStorage()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new IOException("Не удалось определить домашний каталог.");
        FilePath = Path.Combine(home, ".lakecode", "providers.json");
    }

    public ImmutableArray<ProviderProfile> Load()
    {
        ValidatePath(FilePath);
        if (!File.Exists(FilePath))
        {
            var legacy = ProviderProfile.Legacy();
            legacy.Validate();
            return [legacy];
        }
        using var stream = File.OpenRead(FilePath);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Настройки провайдеров превышают 1 MiB.");
        Settings settings;
        try { settings = JsonSerializer.Deserialize<Settings>(stream, Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("Повреждён providers.json. Исправьте файл; настройки не были сброшены."); }
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая версия providers.json.");
        Validate(settings.Providers);
        return settings.Providers;
    }

    public async Task SaveAsync(ImmutableArray<ProviderProfile> profiles, CancellationToken cancellationToken = default)
    {
        Validate(profiles);
        ValidatePath(FilePath);
        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Settings { Providers = profiles }, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Настройки провайдеров превышают 1 MiB.");
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePath(FilePath);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(ImmutableArray<ProviderProfile> profiles)
    {
        if (profiles.IsDefault || profiles.Length > 100 || profiles.Any(profile => profile is null) ||
            profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
            throw new InvalidDataException("Некорректный список провайдеров.");
        foreach (var profile in profiles) profile.Validate();
    }

    private static void ValidatePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("Символические ссылки в пути настроек провайдеров запрещены.");
    }

    private sealed class Settings
    {
        [JsonRequired] public int SchemaVersion { get; init; } = 1;
        [JsonRequired] public ImmutableArray<ProviderProfile> Providers { get; init; } = [];
    }
}
