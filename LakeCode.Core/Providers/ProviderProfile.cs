using System.Collections.Immutable;
using System.Text.Json.Serialization;
using LakeCode.Core.Agent;

namespace LakeCode.Core.Providers;

public enum ProviderKind { OpenAI, Anthropic }
public enum OpenAIApiMode { Responses, ChatCompletions }

/// <summary>Immutable connection snapshot. Never include credentials in conversation state or diagnostics.</summary>
public sealed class ProviderProfile
{
    public const string LegacyId = "00000000-0000-0000-0000-000000000001";
    [JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Revision { get; init; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public ProviderKind Kind { get; init; }
    [JsonRequired] public OpenAIApiMode ApiMode { get; init; }
    [JsonRequired] public string BaseUrl { get; init; } = "";
    [JsonRequired] public string ApiKey { get; init; } = "";
    [JsonRequired] public ImmutableArray<ModelInfo> Models { get; init; } = [];

    public override string ToString() => Name;

    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "D", out _) || !Guid.TryParseExact(Revision, "D", out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 ||
            Name.Any(char.IsControl) || !Enum.IsDefined(Kind) || !Enum.IsDefined(ApiMode))
            throw new InvalidDataException("Некорректный профиль провайдера.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("URL должен быть абсолютным HTTP(S) адресом без ключа, query и fragment.");
        if (ApiKey is null || ApiKey.Length > 8192 || ApiKey.Any(char.IsControl))
            throw new InvalidDataException("Некорректный API-ключ.");
        if (Models.IsDefault || Models.Length > 100 || Models.Any(model => model is null) ||
            Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != Models.Length)
            throw new InvalidDataException("Некорректный список моделей провайдера.");
        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Name) ||
                model.Id.Any(char.IsControl) || model.Name.Any(char.IsControl) ||
                model.ContextWindowTokens <= 0 || model.MaxInputTokens <= 0 || model.MaxOutputTokens <= 0 ||
                model.MaxInputTokens > model.ContextWindowTokens || model.MaxOutputTokens > model.ContextWindowTokens ||
                model.InputModalities.IsDefaultOrEmpty || !model.InputModalities.Contains(ModelModality.Text) ||
                model.InputModalities.Any(value => !Enum.IsDefined(value)) ||
                model.OutputModalities.IsDefaultOrEmpty || model.OutputModalities.Any(value => !Enum.IsDefined(value)) ||
                model.ReasoningEfforts.IsDefaultOrEmpty || model.ReasoningEfforts.Any(value => !ValidEffort(value, model.MaxOutputTokens)))
                throw new InvalidDataException("Некорректные возможности модели. Anthropic: none или budget:N (1024 ≤ N < max output); OpenAI: none, low, medium, high, xhigh, max.");
        }
    }

    private bool ValidEffort(string value, int maxOutput) => Kind == ProviderKind.OpenAI
        ? SupportedEfforts.Contains(value)
        : value == "none" || (value is not null && value.StartsWith("budget:", StringComparison.Ordinal) &&
            int.TryParse(value[7..], out var budget) && budget >= 1024 && budget < maxOutput);

    public static readonly ImmutableArray<string> SupportedEfforts = ["none", "low", "medium", "high", "xhigh", "max"];

    public static ProviderProfile Legacy() => new()
    {
        Id = LegacyId, Revision = LegacyId, Name = "WikiVibe", Kind = ProviderKind.OpenAI, ApiMode = OpenAIApiMode.Responses,
        BaseUrl = "https://api.wikivibe.ru/v1", ApiKey = Environment.GetEnvironmentVariable("WIKIVIBE_API_KEY") ?? "",
        Models = ModelCatalog.Models.Values.OrderBy(model => model.Id, StringComparer.Ordinal).ToImmutableArray()
    };
}
