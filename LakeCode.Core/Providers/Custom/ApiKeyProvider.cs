using System.Collections.Immutable;
using System.Net.Http.Headers;

namespace LakeCode.Core.Providers.Custom;

/// <summary>Shared API-key authentication for generated and custom providers.</summary>
public abstract record ApiKeyProvider(
    string Id, string Name, string BaseUrl,
    string? DiscoveryUrl, string CatalogProviderId, ImmutableArray<string> EnvironmentVariables,
    string? DocumentationUrl, int ModelCount)
    : Provider(Id, Name, BaseUrl, DiscoveryUrl, CatalogProviderId, EnvironmentVariables, DocumentationUrl, ModelCount)
{
    protected static string ApiKey(ProviderProfile profile) => profile.Credentials.GetValueOrDefault("api_key", "");
    public override ImmutableArray<ProviderAuthMethod> AuthenticationMethods => [new("api_key", "API key")];
    public override string AuthenticationMethod(ProviderProfile profile) => "api_key";
    public override ProviderProfile CreateProfile(string name, string credential) => UpdateCredential(new ProviderProfile
    {
        Name = name, DefinitionId = Id
    }, credential);
    public override ProviderProfile UpdateCredential(ProviderProfile profile, string credential) => profile with
    {
        Credentials = ImmutableDictionary<string, string>.Empty.Add("api_key", credential), CredentialState = null
    };
    internal override bool HasCredentials(ProviderProfile profile) => !string.IsNullOrWhiteSpace(ApiKey(profile));
    internal override void Authorize(ProviderProfile profile, HttpRequestMessage request)
    {
        if (!HasCredentials(profile)) throw new InvalidOperationException("Не задан API-ключ. Настройте подключение через /providers.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(profile));
    }
}
