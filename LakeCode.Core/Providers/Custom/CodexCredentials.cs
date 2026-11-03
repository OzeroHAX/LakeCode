using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;

namespace LakeCode.Core.Providers.Custom;

/// <summary>Imported Codex credentials and serialized refresh for requests sharing one login.</summary>
internal sealed class CodexCredentials : IProviderCredentialState
{
    internal const string Method = "codex_cli";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<IProviderCredentialState, ImmutableDictionary<string, string>, Task> _persist;
    private ImmutableDictionary<string, string> _credentials;
    private long _version;
    private bool _needsPersist;

    internal CodexCredentials(ImmutableDictionary<string, string> credentials,
        Func<IProviderCredentialState, ImmutableDictionary<string, string>, Task> persist)
    {
        _credentials = credentials;
        _persist = persist;
    }
    public long Version => Interlocked.Read(ref _version);
    internal ImmutableDictionary<string, string> Current => Volatile.Read(ref _credentials);

    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_needsPersist)
            {
                await _persist(this, Current).ConfigureAwait(false);
                _needsPersist = false;
            }
            if (ExpiresAt(Current["access_token"]) > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token");
            request.Content = JsonContent.Create(new { grant_type = "refresh_token", client_id = ClientId, refresh_token = Current["refresh_token"] });
            // Once the server may consume a one-use refresh token, finish reading and persisting even if the caller cancels.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("Не удалось обновить токены Codex. Повторите импорт из Codex CLI.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var body = await ReadLimitedAsync(stream, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var access = Required(root, "access_token");
            if (ExpiresAt(access) <= DateTimeOffset.UtcNow) throw new InvalidDataException("Codex вернул токен с истёкшим сроком.");
            var next = Current.SetItem("access_token", access);
            if (root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refresh.GetString()))
                next = next.SetItem("refresh_token", refresh.GetString()!);
            if (root.TryGetProperty("id_token", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                next = next.SetItem("id_token", id.GetString()!);
            Volatile.Write(ref _credentials, next);
            Interlocked.Increment(ref _version);
            _needsPersist = true;
            await _persist(this, next).ConfigureAwait(false);
            _needsPersist = false;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidDataException("Превышено время обновления токенов Codex. Повторите импорт при необходимости."); }
        catch (HttpRequestException)
        { throw new InvalidDataException("Не удалось обратиться к службе авторизации Codex."); }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("Некорректный ответ авторизации Codex. Повторите импорт."); }
        finally { _gate.Release(); }
    }

    internal static async Task<ImmutableDictionary<string, string>> ImportAsync(CancellationToken cancellationToken)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var path = Path.Combine(codexHome, "auth.json");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var body = await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("В Codex CLI нет ChatGPT-токенов. Войдите через ChatGPT и повторите импорт.");
            var access = Required(tokens, "access_token");
            var refresh = Required(tokens, "refresh_token");
            var idToken = Required(tokens, "id_token");
            using var identity = DecodeJwt(idToken);
            string? identityAccount = null;
            if (identity.RootElement.TryGetProperty("https://api.openai.com/auth", out var claims) && claims.ValueKind == JsonValueKind.Object)
            {
                if (claims.TryGetProperty("chatgpt_account_is_fedramp", out var fedramp) && fedramp.ValueKind == JsonValueKind.True)
                    throw new InvalidDataException("Импорт аккаунта Codex с маршрутом FedRAMP пока не поддерживается.");
                if (claims.TryGetProperty("chatgpt_account_id", out var value) && value.ValueKind == JsonValueKind.String)
                    identityAccount = value.GetString();
            }
            var account = tokens.TryGetProperty("account_id", out var accountValue) && accountValue.ValueKind == JsonValueKind.String
                ? accountValue.GetString() : null;
            account = string.IsNullOrWhiteSpace(account) ? identityAccount : account;
            if (string.IsNullOrWhiteSpace(account) || account.Length > 512 || account.Any(char.IsControl))
                throw new InvalidDataException("Не удалось определить аккаунт Codex CLI. Повторите вход и импорт.");
            _ = ExpiresAt(access);
            if (tokens.TryGetProperty("oauth_provenance", out var provenance) && provenance.ValueKind == JsonValueKind.Object &&
                ((provenance.TryGetProperty("issuer", out var issuer) && issuer.GetString()?.TrimEnd('/') != "https://auth.openai.com") ||
                 (provenance.TryGetProperty("client_id", out var client) && client.GetString() != ClientId)))
                throw new InvalidDataException("Этот способ входа Codex CLI не поддерживается.");
            return ImmutableDictionary<string, string>.Empty
                .Add("method", Method).Add("access_token", access).Add("refresh_token", refresh)
                .Add("account_id", account).Add("id_token", idToken);
        }
        catch (FileNotFoundException)
        { throw new InvalidDataException("Файл авторизации Codex CLI не найден. При хранении в системном хранилище импорт из файла недоступен."); }
        catch (DirectoryNotFoundException)
        { throw new InvalidDataException("Каталог авторизации Codex CLI не найден."); }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("Не удалось прочитать токены Codex CLI. Войдите снова и повторите импорт."); }
    }

    private static string Required(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(char.IsControl))
            throw new InvalidDataException("В данных авторизации Codex отсутствуют необходимые поля.");
        return value;
    }

    private static DateTimeOffset ExpiresAt(string jwt)
    {
        using var document = DecodeJwt(jwt);
        return DateTimeOffset.FromUnixTimeSeconds(document.RootElement.GetProperty("exp").GetInt64());
    }

    private static JsonDocument DecodeJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new FormatException();
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        return JsonDocument.Parse(Convert.FromBase64String(payload));
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > 128 * 1024) throw new InvalidDataException("Данные авторизации Codex слишком велики.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}
