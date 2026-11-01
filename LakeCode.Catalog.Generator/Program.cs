using System.Net;
using System.Text;
using System.Text.Json;

namespace LakeCode.Catalog.Generator;

internal static class Program
{
    private const string DefaultUrl = "https://models.dev/api.json";
    private const string Usage = """
        Usage: LakeCode.Catalog.Generator --json <path> --output <path> [--url <url>] [--offline]

        --json      Path to the cached models.dev JSON snapshot.
        --output    Path to the generated ModelsDevCatalog.g.cs file.
        --url       models.dev catalog URL (default: https://models.dev/api.json).
        --offline   Do not use the network; use the cached JSON or leave the existing output.
        """;

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var json = await LoadJsonAsync(options);
            if (json is null) return 0;

            EmittedCatalog catalog;
            try
            {
                catalog = CatalogEmitter.Emit(json, options.Url);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                Delete(options.JsonPath);
                Delete(options.EtagPath);
                throw new InvalidDataException($"catalog is invalid: {error.Message}", error);
            }

            var changed = CatalogWriter.WriteIfChanged(options.OutputPath, catalog.Source);
            Console.WriteLine(
                $"models.dev catalog: {catalog.Providers} providers, {catalog.Models} models " +
                $"({(changed ? "updated" : "unchanged")})");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"models.dev catalog: {error.Message}");
            return 1;
        }
    }

    private static async Task<byte[]?> LoadJsonAsync(Options options)
    {
        if (options.Offline)
        {
            if (File.Exists(options.JsonPath)) return await File.ReadAllBytesAsync(options.JsonPath);
            if (File.Exists(options.OutputPath))
            {
                Console.Error.WriteLine("models.dev catalog: offline, using the existing generated file.");
                return null;
            }
            throw new InvalidOperationException($"offline and no cached catalog at {options.JsonPath}.");
        }

        var etag = ReadText(options.EtagPath);
        try
        {
            var response = await DownloadWithRetryAsync(options.Url, etag);
            if (response.Data is null)
            {
                if (File.Exists(options.JsonPath))
                {
                    Console.Error.WriteLine("models.dev catalog: not modified.");
                    return await File.ReadAllBytesAsync(options.JsonPath);
                }

                Console.Error.WriteLine("models.dev catalog: server returned 304 without a cached snapshot; retrying.");
                response = await DownloadWithRetryAsync(options.Url, null);
            }

            if (response.Data is null)
                throw new InvalidOperationException("models.dev returned no catalog body.");

            try
            {
                Validate(response.Data);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                if (File.Exists(options.JsonPath))
                {
                    Console.Error.WriteLine($"models.dev catalog: invalid response ({error.Message}); using the cached snapshot.");
                    return await File.ReadAllBytesAsync(options.JsonPath);
                }
                throw new InvalidDataException("models.dev returned invalid JSON and no cached snapshot exists.", error);
            }

            await WriteAtomicAsync(options.JsonPath, response.Data);
            if (response.ETag is null) Delete(options.EtagPath);
            else await WriteAtomicAsync(options.EtagPath, Encoding.UTF8.GetBytes(response.ETag));
            return response.Data;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (File.Exists(options.JsonPath))
            {
                Console.Error.WriteLine($"models.dev catalog: download failed ({error.Message}); using the cached snapshot.");
                return await File.ReadAllBytesAsync(options.JsonPath);
            }

            if (File.Exists(options.OutputPath))
            {
                Console.Error.WriteLine($"models.dev catalog: download failed ({error.Message}); keeping the existing generated file.");
                return null;
            }

            throw new InvalidOperationException(
                $"could not download {options.Url} and no cached catalog exists; " +
                "connect to the network once or pass --offline after a successful build.", error);
        }
    }

    private static async Task<(byte[]? Data, string? ETag)> DownloadWithRetryAsync(string url, string? etag)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadAsync(url, etag);
            }
            catch (Exception error) when (attempt < 3 && error is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    private static async Task<(byte[]? Data, string? ETag)> DownloadAsync(string url, string? etag)
    {
        using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LakeCode.Catalog.Generator/1.0");
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotModified) return (null, etag);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync();
        if (data.Length == 0) throw new InvalidOperationException("models.dev returned an empty response.");
        return (data, response.Headers.ETag?.ToString());
    }

    private static void Validate(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("models.dev root must be a JSON object.");
    }

    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    private static async Task WriteAtomicAsync(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            Delete(temporary);
        }
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record Options(string Url, string JsonPath, string OutputPath, bool Offline, bool Help)
    {
        public string EtagPath => JsonPath + ".etag";

        public static Options Parse(string[] args)
        {
            string? url = null;
            string? json = null;
            string? output = null;
            var offline = false;
            var help = false;
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string Next()
                {
                    if (index + 1 >= args.Length) throw new ArgumentException($"missing value for {argument}");
                    return args[++index];
                }

                switch (argument)
                {
                    case "--url": url = Next(); break;
                    case "--json": json = Next(); break;
                    case "--output": output = Next(); break;
                    case "--offline": offline = true; break;
                    case "--help" or "-h": help = true; break;
                    default: throw new ArgumentException($"unknown argument: {argument}");
                }
            }

            if (help) return new Options(DefaultUrl, json ?? "", output ?? "", offline, true);
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("--json is required");
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required");
            return new Options(url ?? DefaultUrl, json, output, offline, false);
        }
    }
}
