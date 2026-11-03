using System.Runtime.CompilerServices;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Providers;

/// <summary>Changes transport only between runs; the MAF agent, tools and workspace permissions stay alive.</summary>
internal sealed class ProviderChatClient : IChatClient
{
    private IChatClient? _client;
    private ProviderProfile? _profile;
    private ModelInfo? _model;
    private string? _modelId;
    private string? _sessionId;
    private long _credentialVersion;

    public bool Configure(ProviderProfile profile, ModelInfo model, string sessionId)
    {
        if (ReferenceEquals(profile, _profile) && model.Id == _modelId && sessionId == _sessionId) return false;
        var next = profile.Definition.CreateChatClient(profile, model, sessionId);
        _client?.Dispose();
        _client = next;
        _profile = profile;
        _model = model;
        _modelId = model.Id;
        _sessionId = sessionId;
        _credentialVersion = profile.Definition.CredentialVersion(profile);
        return true;
    }

    private IChatClient Client => _client ?? throw new InvalidOperationException("Подключение ещё не выбрано.");
    private Exception Sanitize(Exception error) => new InvalidOperationException(_profile?.Redact(error.Message) ?? error.Message);

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var profile = _profile ?? throw new InvalidOperationException("Подключение ещё не выбрано.");
        await profile.Definition.PrepareAsync(profile, cancellationToken).ConfigureAwait(false);
        var version = profile.Definition.CredentialVersion(profile);
        if (version == _credentialVersion) return;
        var next = profile.Definition.CreateChatClient(profile, _model!, _sessionId!);
        _client?.Dispose();
        _client = next;
        _credentialVersion = version;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            return await Client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw Sanitize(error); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try { await PrepareAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw Sanitize(error); }
        await using var stream = Client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try { hasNext = await stream.MoveNextAsync(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw Sanitize(error); }
            if (!hasNext) yield break;
            yield return stream.Current;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(IChatClient) ? this : _client?.GetService(serviceType, serviceKey);
    public void Dispose() { _client?.Dispose(); _client = null; _profile = null; _model = null; _sessionId = null; }
}
