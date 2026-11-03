using System.Text.Json;
using System.Net.Http.Headers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using LakeCode.Core.Agent;
using LakeCode.Core.Models;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Providers;

internal interface IProviderCredentialState { long Version { get; } }
public sealed record ProviderAuthMethod(string Id, string Name, bool RequiresImport = false, string? Hint = null);

/// <summary>Provider-specific behavior shared by all operations on a connection.</summary>
public abstract record Provider(
    string Id, string Name, string BaseUrl,
    string? DiscoveryUrl, string CatalogProviderId, ImmutableArray<string> EnvironmentVariables,
    string? DocumentationUrl, int ModelCount)
{
    private static readonly ConditionalWeakTable<Provider, ImmutableDictionary<string, ModelInfo>> ModelsByProvider = new();
    public virtual ImmutableArray<ModelInfo> Models => ModelsDevCatalog.Providers.TryGetValue(CatalogProviderId, out var provider)
        ? provider.Models.Values.OrderBy(model => model.Id, StringComparer.Ordinal)
            .Select(model => ModelsDevCatalog.ToModelInfo(model, ReasoningFormat)).ToImmutableArray() : [];
    internal ModelInfo? FindModel(string id) => ModelsByProvider.GetValue(this,
        provider => provider.Models.ToImmutableDictionary(model => model.Id, StringComparer.Ordinal)).GetValueOrDefault(id);

    public abstract ProviderProfile CreateProfile(string name, string credential);
    public abstract ProviderProfile UpdateCredential(ProviderProfile profile, string credential);
    public abstract ImmutableArray<ProviderAuthMethod> AuthenticationMethods { get; }
    public abstract string AuthenticationMethod(ProviderProfile profile);
    public virtual string EndpointFor(string authenticationMethod) => BaseUrl;
    public virtual Task<ProviderProfile> ImportCredentialsAsync(ProviderProfile profile, string method, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Импорт для этого способа авторизации не поддерживается.");
    internal abstract bool HasCredentials(ProviderProfile profile);
    internal abstract void Authorize(ProviderProfile profile, HttpRequestMessage request);
    internal virtual IEnumerable<string> Secrets(ProviderProfile profile) => profile.Credentials.Values;
    internal virtual IProviderCredentialState? BindCredentials(ProviderProfile profile,
        Func<IProviderCredentialState, ImmutableDictionary<string, string>, Task> persist) => null;
    internal virtual bool SharesCredentials(ProviderProfile existing, ProviderProfile incoming) => false;
    internal virtual Task PrepareAsync(ProviderProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    internal virtual long CredentialVersion(ProviderProfile profile) => profile.CredentialState?.Version ?? 0;

    internal virtual void ValidateProfile(ProviderProfile profile)
    {
        foreach (var (id, rule) in profile.ModelOverrides)
        {
            if (rule.Decision)
            {
                if (!SupportsClassification || !CanSelectDecisionModel(id) || !rule.Manual ||
                    rule.Definition is not null || rule.Overrides is not null || rule.CatalogModelId is not null)
                    throw new InvalidDataException($"Модель решений {id}: некорректные настройки.");
                continue;
            }
            if (!IsModelAllowed(profile, id)) continue;
            var entry = ModelResolver.Resolve(profile, id);
            if (entry.Error is not null) throw new InvalidDataException($"Модель {id}: {entry.Error}");
        }
    }

    public virtual string ProtocolLabel => "Модели";
    public virtual ModelReasoningFormat ReasoningFormat => ModelReasoningFormat.None;
    public virtual bool SupportsChat => false;
    public virtual bool SupportsClassification => false;
    public virtual string? DecisionModelsHint => null;
    public virtual bool SupportsImageGeneration => false;
    public virtual bool SupportsImages(ProviderProfile profile) => SupportsImageGeneration;
    public virtual ImmutableArray<DecisionModelInfo> BuiltInDecisionModels => [];
    public virtual ImmutableArray<DecisionModelInfo> DecisionModels(ProviderProfile profile) => BuiltInDecisionModels;
    public virtual bool CanSelectDecisionModel(string id) => false;
    public virtual DecisionModel CreateDecisionModel(ProviderProfile profile, string id) =>
        throw new InvalidOperationException("Провайдер не поддерживает модели решений.");
    public virtual ImmutableArray<ResolvedProviderModel> ModelEntries(ProviderProfile profile) => SupportsChat ? ModelResolver.ResolveAll(profile) : [];
    public virtual IEnumerable<string> ModelIds(ProviderProfile profile) => Models.Select(model => model.Id)
        .Concat(profile.Discovery?.Ids ?? [])
        .Concat(profile.ModelOverrides.Where(item => !item.Value.Decision).Select(item => item.Key));
    public virtual bool IsModelAllowed(ProviderProfile profile, string id) => true;
    internal virtual bool RequiresKnownCapabilities(ProviderProfile profile) => false;
    internal virtual string ModelsCollection => "data";
    internal virtual string ModelIdField => "id";
    internal virtual string ModelsUrl => DiscoveryUrl ?? BaseUrl.TrimEnd('/') + "/models";
    internal virtual string? NextModelCursor(JsonElement root) => null;
    internal virtual bool ValidEffort(string value, int maxOutput) => value == "none";

    internal void ValidateModel(ModelInfo model)
    {
        if (!SupportsChat || !ProviderModel.ValidId(model.Id) || string.IsNullOrWhiteSpace(model.Name) || model.Name.Any(char.IsControl) ||
            model.ContextWindowTokens <= 0 || model.MaxInputTokens <= 0 || model.MaxOutputTokens <= 0 ||
            model.MaxInputTokens > model.ContextWindowTokens || model.MaxOutputTokens > model.ContextWindowTokens ||
            model.InputModalities.IsDefaultOrEmpty || !model.InputModalities.Contains(ModelModality.Text) ||
            model.InputModalities.Any(value => !Enum.IsDefined(value)) || model.OutputModalities.IsDefaultOrEmpty ||
            model.OutputModalities.Any(value => !Enum.IsDefined(value)) || model.ReasoningEfforts.IsDefaultOrEmpty ||
            model.ReasoningEfforts.Any(value => !ValidEffort(value, model.MaxOutputTokens)))
            throw new InvalidDataException("Некорректные возможности модели или параметры reasoning для выбранного провайдера.");
    }

    internal string Redact(ProviderProfile profile, string text)
    {
        foreach (var secret in Secrets(profile))
            if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    internal virtual IChatClient CreateChatClient(ProviderProfile profile, ModelInfo model, string sessionId) =>
        throw new InvalidOperationException("Провайдер не поддерживает чат.");
    internal virtual ChatOptions Options(ProviderProfile profile, ModelInfo model, string effort, string sessionId, bool title = false) =>
        throw new InvalidOperationException("Провайдер не поддерживает чат.");

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private const int MaxResponseBytes = 1024 * 1024;

    internal virtual async Task<DiscoveredModels> DiscoverAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        if (!profile.HasCredentials) throw new InvalidDataException("Настройте авторизацию для получения моделей.");
        await PrepareAsync(profile, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var ids = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var discoveryBaseUrl = ModelsUrl;
        string? cursor = null;
        try
        {
            do
            {
                var url = discoveryBaseUrl;
                if (cursor is not null) url += (url.Contains('?') ? "&" : "?") + "after_id=" + Uri.EscapeDataString(cursor);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                Authorize(profile, request);
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
                var collection = ModelsCollection;
                if (!root.TryGetProperty(collection, out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Models API не вернул массив {collection}.");
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.GetProperty(ModelIdField).GetString();
                    if (!ProviderModel.ValidId(id)) throw new InvalidDataException("Models API вернул некорректный ID модели.");
                    if (Secrets(profile).Any(secret => !string.IsNullOrEmpty(secret) && id!.Contains(secret, StringComparison.Ordinal)))
                        throw new InvalidDataException("Models API вернул некорректный ID модели.");
                    if (seen.Add(id!)) ids.Add(id!);
                    if (ids.Count > ProviderModel.MaxModels) throw new InvalidDataException("Models API вернул слишком много моделей.");
                }
                cursor = NextModelCursor(root);
                if (cursor is not null && (!ProviderModel.ValidId(cursor) || !cursors.Add(cursor)))
                    throw new InvalidDataException("Models API вернул некорректную пагинацию.");
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
