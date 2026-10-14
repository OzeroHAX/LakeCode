using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LakeCode.Core.Providers;

/// <summary>Models API discovery only. No generation probes, guessed capabilities or credential-bearing redirects.</summary>
public static class ProviderModelDiscovery
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private const int MaxResponseBytes = 1024 * 1024;

    public static async Task<DiscoveredModels> DiscoverAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        if (string.IsNullOrWhiteSpace(profile.ApiKey)) throw new InvalidDataException("Введите API-ключ для получения моделей.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var ids = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            do
            {
                var url = profile.BaseUrl.TrimEnd('/') + (profile.Kind == ProviderKind.Anthropic ? "/v1/models?limit=1000" : "/models");
                if (cursor is not null) url += "&after_id=" + Uri.EscapeDataString(cursor);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (profile.Kind == ProviderKind.Anthropic)
                {
                    request.Headers.Add("x-api-key", profile.ApiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidDataException($"Models API вернул HTTP {(int)response.StatusCode}. Проверьте подключение; при отсутствии API добавьте ID вручную.");
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + count > MaxResponseBytes) throw new InvalidDataException("Ответ Models API превышает 1 MiB.");
                    buffer.Write(chunk, 0, count);
                }
                using var document = JsonDocument.Parse(buffer.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Models API не вернул массив data.");
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    if (!ProviderProfile.ValidModelId(id)) throw new InvalidDataException("Models API вернул некорректный ID модели.");
                    if (seen.Add(id!)) ids.Add(id!);
                    if (ids.Count > ProviderProfile.MaxModels) throw new InvalidDataException("Models API вернул слишком много моделей.");
                }
                cursor = null;
                if (profile.Kind == ProviderKind.Anthropic && root.TryGetProperty("has_more", out var more) && more.GetBoolean())
                {
                    cursor = root.GetProperty("last_id").GetString();
                    if (!ProviderProfile.ValidModelId(cursor) || !cursors.Add(cursor!))
                        throw new InvalidDataException("Models API вернул некорректную пагинацию.");
                }
            } while (cursor is not null);
            return new DiscoveredModels(ids.ToImmutable(), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidDataException("Превышено время ожидания Models API. Предыдущий список сохранён."); }
        catch (HttpRequestException)
        { throw new InvalidDataException("Не удалось обратиться к Models API. Проверьте адрес и доступность подключения; предыдущий список сохранён."); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Некорректный ответ Models API; предыдущий список сохранён."); }
    }
}
