using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using LakeCode.Core.Agent;
using System.Security.Cryptography;

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
        if (!File.Exists(FilePath)) return [];
        using var stream = File.OpenRead(FilePath);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Настройки провайдеров превышают 1 MiB.");
        Settings settings;
        try
        {
            var root = JsonNode.Parse(stream)?.AsObject() ?? throw new JsonException();
            var version = root["SchemaVersion"]?.GetValue<int>() ?? throw new JsonException();
            if (version == 1)
            {
                foreach (var node in root["Providers"]?.AsArray() ?? throw new JsonException())
                {
                    var profile = node?.AsObject() ?? throw new JsonException();
                    var oldModels = profile["Models"]?.Deserialize<ImmutableArray<ModelInfo>>(Options) ?? throw new JsonException();
                    var overrides = oldModels.ToImmutableDictionary(model => model.Id,
                        model => new ModelOverride { Manual = true, Definition = ModelCapabilities.FromModel(model) }, StringComparer.Ordinal);
                    profile.Remove("Models");
                    profile["ModelOverrides"] = JsonSerializer.SerializeToNode(overrides, Options);
                }
                root["SchemaVersion"] = 2;
            }
            settings = root.Deserialize<Settings>(Options) ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Повреждён providers.json. Исправьте файл; настройки не были сброшены."); }
        if (settings.SchemaVersion != 2) throw new InvalidDataException("Неподдерживаемая версия providers.json.");
        Validate(settings.Providers);
        return settings.Providers.Select(profile => profile with { Discovery = LoadDiscovery(profile) }).ToImmutableArray();
    }

    public async Task SaveAsync(ImmutableArray<ProviderProfile> profiles, CancellationToken cancellationToken = default)
    {
        Validate(profiles);
        foreach (var profile in profiles)
            if (profile.Discovery is { } discovery)
                await WriteAtomicAsync(CachePath(profile), new ModelCache(Fingerprint(profile), discovery), cancellationToken);
        await WriteAtomicAsync(FilePath, new Settings { Providers = profiles }, cancellationToken);
    }

    /// <summary>Updates discovery only; credentials, overrides and connection revision are unchanged.</summary>
    public Task SaveDiscoveryAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var discovery = profile.Discovery ?? throw new InvalidDataException("Список моделей отсутствует.");
        return WriteAtomicAsync(CachePath(profile), new ModelCache(Fingerprint(profile), discovery), cancellationToken);
    }

    private DiscoveredModels? LoadDiscovery(ProviderProfile profile)
    {
        var path = CachePath(profile);
        ValidatePath(path);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Кэш моделей превышает 1 MiB.");
        var cache = JsonSerializer.Deserialize<ModelCache>(stream, Options)
            ?? throw new InvalidDataException("Повреждён кэш моделей.");
        if (cache.ConnectionFingerprint != Fingerprint(profile)) return null;
        if (cache.Discovery is null) throw new InvalidDataException("Повреждён кэш моделей.");
        (profile with { Discovery = cache.Discovery }).Validate();
        return cache.Discovery;
    }

    private sealed record ModelCache(string ConnectionFingerprint, DiscoveredModels Discovery);

    private static string Fingerprint(ProviderProfile profile) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { profile.Kind, BaseUrl = profile.BaseUrl.TrimEnd('/'), profile.ApiKey })));

    private string CachePath(ProviderProfile profile) => Path.Combine(Path.GetDirectoryName(FilePath)!,
        "model-cache", profile.Id, profile.Revision + ".json");

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
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
            ValidatePath(path);
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
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
        [JsonRequired] public int SchemaVersion { get; init; } = 2;
        [JsonRequired] public ImmutableArray<ProviderProfile> Providers { get; init; } = [];
    }
}
