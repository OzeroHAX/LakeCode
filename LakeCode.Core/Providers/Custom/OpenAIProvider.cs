using System.ClientModel.Primitives;
using System.Collections.Immutable;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;
using OpenAI;
using System.Text.Json;

namespace LakeCode.Core.Providers.Custom;

/// <summary>OpenAI Responses with either an API key or imported Codex CLI credentials.</summary>
internal sealed record OpenAIProvider(ModelsDevProvider Catalog, string Endpoint, string? ModelsEndpoint)
    : OpenAICompatibleProvider(Catalog.Id, Catalog.Name, OpenAIApiMode.Responses, Endpoint, ModelsEndpoint,
        Catalog.Id, Catalog.EnvironmentVariables, Catalog.DocumentationUrl, Catalog.Models.Count)
{
    private const string CodexEndpoint = "https://chatgpt.com/backend-api/codex";
    // Protocol compatibility target: Codex CLI 0.166.0.
    private const string CodexClientVersion = "0.166.0";
    private static readonly HttpClient ModelsClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    public override ImmutableArray<ProviderAuthMethod> AuthenticationMethods =>
        [new("api_key", "API key"), new(CodexCredentials.Method, "Codex CLI", true,
            "Обновление общего токена в Codex CLI или другой копии LakeCode может потребовать повторного входа.")];
    public override string AuthenticationMethod(ProviderProfile profile) => IsCodex(profile) ? CodexCredentials.Method : "api_key";
    public override async Task<ProviderProfile> ImportCredentialsAsync(ProviderProfile profile, string method, CancellationToken cancellationToken)
    {
        if (method != CodexCredentials.Method) throw new InvalidOperationException("Неизвестный способ авторизации.");
        return profile with { Credentials = await CodexCredentials.ImportAsync(cancellationToken).ConfigureAwait(false), CredentialState = null, Discovery = null };
    }
    internal override bool HasCredentials(ProviderProfile profile) => IsCodex(profile)
        ? profile.Credentials.TryGetValue("access_token", out var access) && !string.IsNullOrWhiteSpace(access) &&
          profile.Credentials.TryGetValue("refresh_token", out var refresh) && !string.IsNullOrWhiteSpace(refresh) &&
          profile.Credentials.TryGetValue("account_id", out var account) && !string.IsNullOrWhiteSpace(account)
        : base.HasCredentials(profile);
    internal override IEnumerable<string> Secrets(ProviderProfile profile) => IsCodex(profile)
        ? profile.Credentials.Where(item => item.Key is "access_token" or "refresh_token" or "id_token" or "account_id").Select(item => item.Value)
            .Concat(((profile.CredentialState as CodexCredentials)?.Current ?? profile.Credentials)
                .Where(item => item.Key is "access_token" or "refresh_token" or "id_token" or "account_id").Select(item => item.Value))
        : base.Secrets(profile);
    internal override IProviderCredentialState? BindCredentials(ProviderProfile profile,
        Func<IProviderCredentialState, ImmutableDictionary<string, string>, Task> persist) =>
        IsCodex(profile) ? new CodexCredentials(profile.Credentials, persist) : null;
    internal override bool SharesCredentials(ProviderProfile existing, ProviderProfile incoming) => IsCodex(existing) && IsCodex(incoming) && HasCredentials(existing) && HasCredentials(incoming) &&
        existing.Credentials.GetValueOrDefault("refresh_token") == incoming.Credentials.GetValueOrDefault("refresh_token") &&
        existing.Credentials.GetValueOrDefault("account_id") == incoming.Credentials.GetValueOrDefault("account_id");
    internal override Task PrepareAsync(ProviderProfile profile, CancellationToken cancellationToken) => IsCodex(profile)
        ? (profile.CredentialState as CodexCredentials ?? throw new InvalidOperationException("Сохраните импортированные данные Codex перед использованием.")).PrepareAsync(cancellationToken)
        : Task.CompletedTask;
    public override string EndpointFor(string authenticationMethod) => authenticationMethod == CodexCredentials.Method ? CodexEndpoint : BaseUrl;
    internal override string Credential(ProviderProfile profile) => IsCodex(profile)
        ? Current(profile).GetValueOrDefault("access_token", "") : base.Credential(profile);
    public override bool SupportsImages(ProviderProfile profile) => !IsCodex(profile) && base.SupportsImages(profile);
    public override IEnumerable<string> ModelIds(ProviderProfile profile) => IsCodex(profile)
        ? profile.Discovery?.Ids ?? [] : base.ModelIds(profile);
    public override bool IsModelAllowed(ProviderProfile profile, string id) => !IsCodex(profile) ||
        profile.Discovery?.Ids.Contains(id, StringComparer.Ordinal) == true;
    internal override bool RequiresKnownCapabilities(ProviderProfile profile) => IsCodex(profile);

    internal override PipelinePolicy? CreateResponseHeadersPolicy(ProviderProfile profile, ModelInfo model,
        Action<ProviderResponseRouting>? responseReceived) =>
        IsCodex(profile) && responseReceived is not null
            ? new CodexResponseHeadersPolicy(model.Id, responseReceived)
            : null;

    internal override void ConfigureOpenAI(OpenAIClientOptions options, ProviderProfile profile, string sessionId)
    {
        if (IsCodex(profile)) options.AddPolicy(new CodexAccountHeaders(Current(profile)["account_id"]), PipelinePosition.PerCall);
    }
    internal override void Authorize(ProviderProfile profile, HttpRequestMessage request)
    {
        if (!IsCodex(profile)) { base.Authorize(profile, request); return; }
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Credential(profile));
        request.Headers.Add("ChatGPT-Account-Id", Current(profile)["account_id"]);
        request.Headers.Add("originator", "lakecode");
    }
    internal override ChatOptions Options(ProviderProfile profile, ModelInfo model, string effort, string sessionId, bool title = false)
    {
        var options = base.Options(profile, model, effort, sessionId, title);
        if (IsCodex(profile)) options.MaxOutputTokens = null;
        return options;
    }
    internal override async Task<DiscoveredModels> DiscoverAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        if (!IsCodex(profile)) return await base.DiscoverAsync(profile, cancellationToken).ConfigureAwait(false);
        await PrepareAsync(profile, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CodexEndpoint + "/models?client_version=" + CodexClientVersion);
            Authorize(profile, request);
            using var response = await ModelsClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Каталог Codex вернул HTTP {(int)response.StatusCode}. Предыдущий список сохранён.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Каталог Codex превышает допустимый размер.");
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var models = document.RootElement.GetProperty("models");
            if (models.ValueKind != JsonValueKind.Array) throw new JsonException();
            var ids = ImmutableArray.CreateBuilder<string>();
            var capabilities = ImmutableDictionary.CreateBuilder<string, ModelCapabilities>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in models.EnumerateArray())
            {
                if (item.TryGetProperty("visibility", out var visibility) && visibility.GetString() != "list") continue;
                var id = item.GetProperty("slug").GetString();
                if (!ProviderModel.ValidId(id)) throw new JsonException();
                if (Secrets(profile).Any(secret => !string.IsNullOrEmpty(secret) && id!.Contains(secret, StringComparison.Ordinal))) throw new JsonException();
                if (!seen.Add(id!)) continue;
                if (ids.Count >= ProviderModel.MaxModels) throw new InvalidDataException("Каталог Codex содержит слишком много моделей.");
                ids.Add(id!);
                var inputs = ImmutableArray.CreateBuilder<ModelModality>();
                inputs.Add(ModelModality.Text);
                if (item.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array &&
                    modalities.EnumerateArray().Any(modality => modality.GetString() == "image")) inputs.Add(ModelModality.Image);
                var efforts = ImmutableArray.CreateBuilder<string>();
                if (item.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                    foreach (var level in levels.EnumerateArray())
                        if (level.TryGetProperty("effort", out var value) && value.ValueKind == JsonValueKind.String &&
                            value.GetString() is { } effort && SupportedEfforts.Contains(effort) && !efforts.Contains(effort)) efforts.Add(effort);
                if (efforts.Count == 0) efforts.Add("none");
                int? context = item.TryGetProperty("context_window", out var window) && window.ValueKind == JsonValueKind.Number &&
                    window.TryGetInt32(out var contextValue) && contextValue > 0 ? contextValue : null;
                var name = item.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null;
                if (name is not null && Secrets(profile).Any(secret => !string.IsNullOrEmpty(secret) && name.Contains(secret, StringComparison.Ordinal))) throw new JsonException();
                capabilities[id!] = new ModelCapabilities { Name = name, ContextWindowTokens = context,
                    InputModalities = inputs.ToImmutable(), ReasoningEfforts = efforts.ToImmutable() };
            }
            return new DiscoveredModels(ids.ToImmutable(), DateTimeOffset.UtcNow) { Capabilities = capabilities.ToImmutable() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidDataException("Превышено время ожидания каталога Codex. Предыдущий список сохранён."); }
        catch (HttpRequestException)
        { throw new InvalidDataException("Не удалось получить каталог Codex. Предыдущий список сохранён."); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Некорректный каталог Codex. Предыдущий список сохранён."); }
    }

    private static bool IsCodex(ProviderProfile profile) => profile.Credentials.GetValueOrDefault("method") == CodexCredentials.Method;
    private static ImmutableDictionary<string, string> Current(ProviderProfile profile) =>
        (profile.CredentialState as CodexCredentials)?.Current ?? profile.Credentials;

    private sealed class CodexAccountHeaders(string accountId) : PipelinePolicy
    {
        private void SetHeaders(PipelineMessage message)
        {
            message.Request.Headers.Set("ChatGPT-Account-Id", accountId);
            message.Request.Headers.Set("originator", "lakecode");
        }
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            SetHeaders(message);
            ProcessNext(message, pipeline, currentIndex);
        }
        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            SetHeaders(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}
