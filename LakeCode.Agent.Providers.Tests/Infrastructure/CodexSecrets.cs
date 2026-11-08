using System.Text.Json;
using LakeCode.Agent.Providers.Custom;

namespace LakeCode.Agent.Providers.Tests;

/// <summary>Real Codex credentials and settings read from the git-ignored secrets file copied into the build output.</summary>
internal sealed record CodexSecrets(string AccessToken, string AccountId, string Model, TimeSpan Timeout)
{
    private const char Placeholder = '<';

    internal static CodexSecrets Require() =>
        TryLoad() ?? throw new InvalidOperationException(
            "Secrets/codex.json disappeared after test discovery. " +
            "Copy Secrets/codex.example.json to Secrets/codex.json and fill in AccessToken, AccountId and Model.");

    internal static CodexSecrets? TryLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Secrets", "codex.json");
        if (!File.Exists(path)) return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (!root.TryGetProperty("CodexConfig", out var config)) return null;
        var accessToken = ReadString(config, "AccessToken");
        var accountId = ReadString(config, "AccountId");
        if (accessToken is null || accountId is null) return null;

        if (!root.TryGetProperty("CodexTests", out var tests)) return null;
        var model = ReadString(tests, "Model");
        if (model is null) return null;

        var timeoutSeconds = tests.TryGetProperty("TimeoutSeconds", out var seconds) &&
            seconds.TryGetInt32(out var value) && value > 0
                ? value
                : 180;

        return new CodexSecrets(accessToken, accountId, model, TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) || text.StartsWith(Placeholder) ? null : text;
    }
}

/// <summary>Supplies the credentials from the secrets file instead of ~/.lakecode/providers.json.</summary>
internal sealed class CodexSecretsConfigSource(CodexSecrets secrets) : IProviderConfigSource
{
    public T Load<T>()
        where T : class =>
        new CodexConfig { AccessToken = secrets.AccessToken, AccountId = secrets.AccountId } as T
            ?? throw new InvalidOperationException($"Configuration '{typeof(T).Name}' is not supported by the test source.");
}

public sealed class CodexIntegrationAttribute : FactAttribute
{
    private const string MissingSecrets =
        "Codex integration tests need Secrets/codex.json in the build output. " +
        "Copy Secrets/codex.example.json to Secrets/codex.json and fill in AccessToken, AccountId and Model.";

    public CodexIntegrationAttribute()
    {
        if (CodexSecrets.TryLoad() is null) Skip = MissingSecrets;
    }
}
