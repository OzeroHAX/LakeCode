using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using LakeCode.Core.Models;

namespace LakeCode.Core.Providers.Custom;

/// <summary>OpenRouter supports chat and a separate catalog and endpoint for decision models.</summary>
internal sealed record OpenRouterProvider(ModelsDevProvider Catalog, string Endpoint, string? ModelsEndpoint)
    : OpenAICompatibleProvider(Catalog.Id, Catalog.Name, OpenAIApiMode.ChatCompletions, Endpoint, ModelsEndpoint, Catalog.Id,
        Catalog.EnvironmentVariables, Catalog.DocumentationUrl, Catalog.Models.Count)
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private const int MaxResponseBytes = 1024 * 1024;
    public override bool SupportsClassification => true;
    public override bool CanSelectDecisionModel(string id) => ProviderModel.ValidId(id) && !id.StartsWith('~');
    public override ImmutableArray<DecisionModelInfo> DecisionModels(ProviderProfile profile) =>
        BuiltInDecisionModels.Select(model => model.Id).Concat(profile.Discovery?.DecisionIds ?? []).Concat(profile.ModelOverrides.Where(item => item.Value.Decision && item.Value.Manual).Select(item => item.Key))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new DecisionModelInfo(id, CanSelectDecisionModel(id))).ToImmutableArray();
    public override DecisionModel CreateDecisionModel(ProviderProfile profile, string id) =>
        DecisionModels(profile).Any(model => model.Id == id && model.IsSelectable)
            ? new SystemOneModel(profile, id, new Uri("https://openrouter.ai/api/alpha/decisions"))
            : throw new InvalidOperationException("Модель решений недоступна. Обновите список моделей.");

    internal override async Task<DiscoveredModels> DiscoverAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        var chatModels = await base.DiscoverAsync(profile, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models?output_modalities=decisions");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Authorize(profile, request);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Каталог моделей решений вернул HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaxResponseBytes) throw new InvalidDataException("Каталог моделей решений превышает 1 MiB.");
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var data = document.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array) throw new JsonException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("alias_target", out var alias) && alias.ValueKind != JsonValueKind.Null) continue;
                if (!item.TryGetProperty("architecture", out var architecture) ||
                    !architecture.TryGetProperty("output_modalities", out var modalities) || modalities.ValueKind != JsonValueKind.Array ||
                    !modalities.EnumerateArray().Any(value => value.GetString() == "decisions")) continue;
                var id = item.GetProperty("canonical_slug").GetString();
                if (!ProviderModel.ValidId(id)) throw new JsonException();
                if (Secrets(profile).Any(secret => !string.IsNullOrEmpty(secret) && id!.Contains(secret, StringComparison.Ordinal))) throw new JsonException();
                if (CanSelectDecisionModel(id!)) ids.Add(id!);
                if (ids.Count > ProviderModel.MaxModels) throw new InvalidDataException("Каталог моделей решений слишком велик.");
            }
            return chatModels with { DecisionIds = ids.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidDataException("Превышено время ожидания каталога моделей решений. Предыдущий список сохранён."); }
        catch (HttpRequestException)
        { throw new InvalidDataException("Не удалось получить каталог моделей решений. Предыдущий список сохранён."); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Некорректный каталог моделей решений. Предыдущий список сохранён."); }
    }
}
